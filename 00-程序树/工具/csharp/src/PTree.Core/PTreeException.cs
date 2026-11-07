namespace PTree.Core;

/// <summary>内核可预期的用法 / IO 错误；外壳据此返回退出码 2。</summary>
public sealed class PTreeException : Exception
{
    public PTreeException(string message) : base(message)
    {
    }

    public PTreeException(string message, Exception inner) : base(message, inner)
    {
    }
}
