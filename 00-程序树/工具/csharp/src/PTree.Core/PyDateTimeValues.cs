namespace PTree.Core;

/// <summary>PyYAML 解析出的 datetime.date。</summary>
public sealed record PyDate(int Year, int Month, int Day)
{
    public string Iso => string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"{Year:0000}-{Month:00}-{Day:00}");

    public override string ToString() => Iso;
}

/// <summary>PyYAML 解析出的 datetime.datetime（Iso 为 Python isoformat 结果）。</summary>
public sealed record PyDateTime(string Iso)
{
    public override string ToString() => Iso;
}
