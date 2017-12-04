using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Swagify
{
    public static class CompilerExtensions
    {
        public static IdentifierNameSyntax GetName(this ExpressionSyntax exp)
        {
            return (exp as MemberAccessExpressionSyntax)?.Name as IdentifierNameSyntax;
        }

        public static string GetName(this AttributeListSyntax att)
        {
            return (att.Attributes[0].Name as IdentifierNameSyntax)?.Identifier.ValueText;
        }
    }
}
