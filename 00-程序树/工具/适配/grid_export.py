# -*- coding: utf-8 -*-
"""生成可视化网格数据。临时实现：内核 export 子命令落地后由它取代，产物格式不变。"""
import json, re, sys, datetime, pathlib
import yaml

import argparse
_ap = argparse.ArgumentParser(description="从 program-tree.yaml 与 树实例 生成画布数据 data.js")
_ap.add_argument("--root", default=".", help="树根目录（里面有 00-程序树/program-tree.yaml）")
_ap.add_argument("--out", default="", help="输出文件；默认 <root>/00-程序树/工具/viewer/data.js")
_args = _ap.parse_args()
ROOT = pathlib.Path(_args.root).resolve()
TREE = ROOT / "00-程序树"
OUT = pathlib.Path(_args.out) if _args.out else TREE / "工具" / "viewer" / "data.js"

def parse_rows(path):
    text = path.read_text(encoding="utf-8")
    lines = [l.rstrip("\n") for l in text.splitlines()]
    fields = None
    for l in lines:
        if "每行字段顺序：" in l:
            raw = l.split("每行字段顺序：", 1)[1]
            fields = [p.strip().rstrip("。") for p in raw.split("｜")]
            break
    rows = []
    if not fields:
        return fields, rows
    for l in lines:
        if "｜" not in l or l.startswith("每行"):
            continue
        cells = [c.strip() for c in l.split("｜")]
        if len(cells) < 2:
            continue
        # 只有首列像编号的行才算条目：字母开头，后接字母数字点横线；表头与版本行一律跳过
        if not re.match(r"^[A-Za-z][A-Za-z0-9._-]*$", cells[0]):
            continue
        row = {f: (cells[i] if i < len(cells) else "") for i, f in enumerate(fields)}
        row["_cells"] = cells
        rows.append(row)
    return fields, rows

man = yaml.safe_load((TREE / "program-tree.yaml").read_text(encoding="utf-8"))
nodes = man["nodes"]
groups = man.get("groups", [])
phases = man.get("phases", [])

grid_defs = [
    ("program", "程序", "树实例/程序树.txt", "id"),
    ("plan", "策划", "树实例/策划树.txt", "编号"),
    ("art", "美术", "树实例/美术树.txt", "编号"),
    ("ui", "UI", "树实例/UI树.txt", "编号"),
]

# 清单 trees: 段登记的预留网格，一律从清单读，不在这里写死
extra_defs = []
for tree in (man.get("trees") or []):
    extra_defs.append(("extra:" + tree["kind"], tree["kind"], "树实例/%s.txt" % tree["file"], "编号"))
grid_defs = grid_defs + extra_defs

grids = []
for kind, name, rel, idf in grid_defs:
    fields, rows = parse_rows(TREE / rel)
    grids.append({"kind": kind, "name": name, "instance": rel, "fields": fields or [], "rows": rows})
    print(kind, "fields", fields, "rows", len(rows), file=sys.stderr)

by_id = {n["id"]: n for n in nodes}

# 短介绍：00-程序树/短介绍/<id>.txt，一行写功能；视图与 AI 粗读共用同一份
SHORT_DIR = TREE / "短介绍"
DOC_DIR = TREE / "说明"


def read_short(ident, limit=80):
    """读 <短介绍>/<id>.txt，压成一行，超长截断；没有就空串。"""
    path = SHORT_DIR / (str(ident) + ".txt")
    if not path.exists():
        return ""
    return path.read_text(encoding="utf-8").strip().replace("\n", " ")[:limit]


for n in nodes:
    s = read_short(n.get("id"))
    if s:
        n["short"] = s

# 网格条目（策划 / 美术 / UI / 全文件）也带短介绍，字段名与 nodes 一样用 short
for grid in grids:
    key = "id" if grid["kind"] == "program" else "编号"
    for row in grid["rows"]:
        rid = (row.get(key) or "").strip()
        if not rid:
            continue
        s = read_short(rid)
        if s:
            row["short"] = s

links = []
def add(a, b, t, note=""):
    if a and b:
        links.append({"from": a, "to": b, "type": t, "note": note})

for n in nodes:
    if n.get("parent"):
        add(n["parent"], n["id"], "contains")
    for d in n.get("depends_on") or []:
        add(n["id"], d, "depends_on")
# 契约流：产出方 -> 消费方
producer = {}
for n in nodes:
    for c in n.get("outputs") or []:
        producer.setdefault(c, []).append(n["id"])
for n in nodes:
    for c in n.get("inputs") or []:
        for p in producer.get(c, []):
            if p != n["id"]:
                add(p, n["id"], "flow", c)

# 策划 -> 程序
plan_grid = next(g for g in grids if g["kind"] == "plan")
nplan = 0
for row in plan_grid["rows"]:
    targets = row.get("对应程序", "")
    for t in re.split(r"[、,，/ ]+", targets):
        t = t.strip()
        if t in by_id:
            add(row.get("编号"), t, "plans", row.get("名称", ""))
            nplan += 1

# 程序 -> 策划（反向留观察）
print("plans links", nplan, file=sys.stderr)


# ---- 功能块（打组）：program-tree.yaml 的 groups 段，按网格成员裁剪 ----
def group_one_line(group):
    """一行简介：优先取 说明/<组id>.txt 的第一句，没有就用清单里的 description。"""
    doc = DOC_DIR / (str(group["id"]) + ".txt")
    if doc.exists():
        text = doc.read_text(encoding="utf-8").strip().replace("\n", " ")
        head = re.split(r"[。！？]", text, 1)[0].strip()
        if head:
            return head[:60]
    return (group.get("description") or "").strip()[:60]


BLOCKS = []
for g in groups:
    members = [m for m in (g.get("members") or []) if m in by_id]
    if not members:
        continue
    BLOCKS.append({"id": g["id"], "name": g.get("name") or g["id"],
                   "description": group_one_line(g), "members": members,
                   "parent": g.get("parent") or None})
BLOCK_IDS = {b["id"] for b in BLOCKS}


def blocks_for(node_ids):
    """只保留这张网格里存在的成员；父组不在本网格就置空。"""
    out = []
    for b in BLOCKS:
        members = [m for m in b["members"] if m in node_ids]
        if not members:
            continue
        out.append({"id": b["id"], "name": b["name"], "description": b["description"],
                    "members": members,
                    "parent": b["parent"] if b["parent"] in BLOCK_IDS else None})
    return out


# 组 -> 节点 也是一种联系，写进 links（type=block），画布据此算组框包围盒
nblock = 0
for b in BLOCKS:
    for m in b["members"]:
        add(b["id"], m, "block", b["name"])
        nblock += 1
print("block links", nblock, "blocks", len(BLOCKS), file=sys.stderr)

# ---- 每张网格的 graph：节点 + 父子 + 交叉联系，供视图统一按"树"画 ----
def graph_for_program():
    ns, ls = [], []
    for n in nodes:
        ns.append({"id": n["id"], "name": n.get("name") or n["id"], "parent": n.get("parent"),
                   "layer": n.get("layer", 0), "status": n.get("status"), "kind": n.get("kind"),
                   "short": n.get("short", "")})
    for l in links:
        if l["type"] == "contains":
            continue
        ls.append(l)
    ids = {n["id"] for n in nodes}
    return {"root": man.get("root"), "nodes": ns, "links": ls, "blocks": blocks_for(ids)}


def row_id(row, grid_kind):
    key = "id" if grid_kind == "program" else "编号"
    return (row.get(key) or "").strip()


def build_row_graph(grid, root_name, root_id):
    fields = grid["fields"] or []
    rows = [r for r in grid["rows"] if row_id(r, grid["kind"])]
    ids = {row_id(r, grid["kind"]) for r in rows}
    ns = []
    for r in rows:
        rid = row_id(r, grid["kind"])
        cells = r.get("_cells") or []
        parent = None
        # 组行：编号 ｜ 名称 ｜ 父 ｜ 子集 ｜ 说明
        if len(fields) >= 6 and 0 < len(cells) < len(fields) - 1 and len(cells) > 2:
            cand = cells[2].strip()
            if cand in ids and cand != rid:
                parent = cand
        if parent is None:
            for field in ("组", "分类", "父子集"):
                raw = (r.get(field) or "").strip()
                if not raw:
                    continue
                cand = raw.replace("父集", "").strip()
                for token in re.split(r"[、,，/ 　]+", cand):
                    token = token.strip()
                    if token in ids and token != rid:
                        parent = token
                        break
                if parent:
                    break
        if parent is None and "." in rid:
            cand = rid.rsplit(".", 1)[0]
            if cand in ids:
                parent = cand
        ns.append({"id": rid, "name": (r.get("名称") or rid), "parent": parent, "layer": 0,
                   "status": (r.get("状态") or None), "fields": r, "short": (r.get("short") or ""),
                   "kind": "root" if rid == root_id else "item"})
    # 数据没有单根时补一个视觉根，标 synthetic，不冒充真实条目
    if root_id not in ids:
        orphans = [n for n in ns if n["parent"] is None or n["parent"] not in ids]
        if len(orphans) == 1:
            # 只有一个无父条目时它本身就是根，不额外加壳
            orphans[0]["kind"] = "root"
            root_id = orphans[0]["id"]
        elif orphans or not ns:
            ns.insert(0, {"id": root_id, "name": root_name, "parent": None, "layer": 0, "status": None,
                          "fields": {}, "kind": "root", "synthetic": True})
            for n in orphans:
                n["parent"] = root_id
    depth = {n["id"]: 0 for n in ns}
    changed, guard = True, 0
    while changed and guard < 20:
        changed, guard = False, guard + 1
        for n in ns:
            p = n.get("parent")
            if p in depth and depth[n["id"]] < depth[p] + 1:
                depth[n["id"]] = depth[p] + 1
                n["layer"] = depth[n["id"]]
                changed = True
    blocks = blocks_for(ids)
    block_links = []
    for b in blocks:
        for m in b["members"]:
            block_links.append({"from": b["id"], "to": m, "type": "block", "note": b["name"]})
    return {"root": root_id, "nodes": ns, "links": block_links, "blocks": blocks}


graphs = {}
graphs["program"] = graph_for_program()
for grid in grids:
    if grid["kind"] == "program":
        grid["graph"] = graphs["program"]
        continue
    grid["graph"] = build_row_graph(grid, grid["name"], "root:%s" % grid["kind"])

data = {
    "meta": {
        "generated_at": datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
        "tree_version": man.get("tree_version"),
        "updated": str(man.get("updated")),
        "source": "program-tree.yaml + 树实例/*.txt",
        "generator": "工具/适配/grid_export.py（临时）",
    },
    "grids": grids,
    "nodes": nodes,
    "groups": groups,
    "phases": [{"id": p["id"], "name": p["name"]} for p in phases],
    "links": links,
}
OUT.parent.mkdir(parents=True, exist_ok=True)
OUT.write_text("window.GRID_DATA = " + json.dumps(data, ensure_ascii=False, separators=(",", ":")) + ";\n", encoding="utf-8")
print("wrote", OUT, OUT.stat().st_size, "bytes; nodes", len(nodes), "links", len(links), file=sys.stderr)
