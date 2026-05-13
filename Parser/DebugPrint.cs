using System.Text;

using Fizzle.Data;

namespace Fizzle.Parser;

public static class DebugPrint
{
    public static string PrintAstNode(AstNode.Base node)
    {
        var sb = new StringBuilder();

        node.DebugPrint(sb, 0);

        return sb.ToString();
    }
}