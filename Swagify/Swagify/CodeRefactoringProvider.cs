using System;
using System.Collections.Generic;
using System.Composition;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;

namespace Swagify
{
    [ExportCodeRefactoringProvider(LanguageNames.CSharp, Name = nameof(SwagifyCodeRefactoringProvider)), Shared]
    internal class SwagifyCodeRefactoringProvider : CodeRefactoringProvider
    {
        private const string DescriptionPlaceholder = "TODO: Response description";
        private const string AttributeName = "SwaggerResponse";
        private static readonly string StatusCodeEnumName = typeof(HttpStatusCode).Name;
        public sealed override async Task ComputeRefactoringsAsync(CodeRefactoringContext context)
        {
            /* Check if we're in a controller. Return if it isn't. */
            if (!context.Document.Name.EndsWith("Controller.cs"))
                return;

            SyntaxNode root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);

            // Find the node at the selection.
            SyntaxNode node = root.FindNode(context.Span);

            // Only offer a refactoring if the selected node is a type declaration node.
            MethodDeclarationSyntax methodDecl = node as MethodDeclarationSyntax;
            if (methodDecl == null)
            {
                return;
            }

            // For any type declaration node, create a code action to reverse the identifier text.
            CodeAction action = CodeAction.Create("Swagify endpoint", c => SwagifyEndpoint(context.Document, methodDecl, c));

            // Register this code action.
            context.RegisterRefactoring(action);
        }

        private async Task<Solution> SwagifyEndpoint(Document doc, MethodDeclarationSyntax method,
            CancellationToken cancellationToken)
        {
            SyntaxNode root = await doc.GetSyntaxRootAsync(cancellationToken);
            BlockSyntax methodBlock = method.ChildNodes().First(m => m is BlockSyntax) as BlockSyntax;

            /* Get SwaggerResponse attributes:
             * att.Attributes[0] is always the name of the attribute
             * (I think, it could be something like assembly:) TODO */
            List<AttributeListSyntax> swaggerResponses = new List<AttributeListSyntax>();
            List<AttributeListSyntax> remainingAttributes = new List<AttributeListSyntax>();
            foreach (AttributeListSyntax att in method.AttributeLists)
            {
                if (att.GetName() == AttributeName)
                    swaggerResponses.Add(att);
                else
                    remainingAttributes.Add(att);
            }

            /* TODO: check BlockSyntaxes for returns */
            List<ReturnStatementSyntax> returnStatements = methodBlock.ChildNodes().OfType<ReturnStatementSyntax>().ToList();

            Dictionary<HttpStatusCode, AttributeMetadata> responses = GetAttributeSummaries(swaggerResponses);
            /* Clone dictionary for working, keep returnTypes for the original */
            Dictionary<HttpStatusCode, AttributeMetadata> responsesUpdated =
                responses.ToDictionary(a => a.Key, b => new AttributeMetadata(b.Value.StatusCode, b.Value.Description, b.Value.TypeName));

            for (int index = 0; index < returnStatements.Count; index++)
            {
                ReturnStatementSyntax returnStatement = returnStatements[index];
                HttpStatusCode statusCode = GetReturnCode(returnStatement);
                /* Get the type and update it. If the types are the same,
                 * nothing will change. */
                string type = GetReturnTypeName(returnStatement);
                if (responsesUpdated.ContainsKey(statusCode))
                    responsesUpdated[statusCode].TypeName = type;
                else
                    responsesUpdated[statusCode] =
                        new AttributeMetadata(statusCode, DescriptionPlaceholder, type);
            }
            /* Check if a change is required */

            if (!IsRefactorRequired(responses, responsesUpdated))
            {
                /* No change required */
                return null;
            }

            /* Commit refactoring */
            SyntaxList<AttributeListSyntax> newAttributes = DoRefactor(responsesUpdated, method);

            Solution swagSolution = doc.WithSyntaxRoot(root.ReplaceNode(
                method, method.WithAttributeLists(newAttributes))).Project.Solution;

            return swagSolution;
        }

        /// <summary>
        /// Checks if a refactor is required for the method
        /// </summary>
        /// <param name="original">The original set of attributes</param>
        /// <param name="updated">The attributes as they should be according to the return values</param>
        /// <returns>
        /// True if a refactor is required.
        /// False if the two dictionaries contain the same StatusCode/Type combinations.
        /// </returns>
        private bool IsRefactorRequired(Dictionary<HttpStatusCode, AttributeMetadata> original,
            Dictionary<HttpStatusCode, AttributeMetadata> updated)
        {
            if (original.Count != updated.Count)
                return true;
            Dictionary<HttpStatusCode, AttributeMetadata>.KeyCollection originalKeys = original.Keys;
            Dictionary<HttpStatusCode, AttributeMetadata>.KeyCollection updatedKeys = updated.Keys;
            foreach (HttpStatusCode code in originalKeys)
            {
                AttributeMetadata originalAttribute = original[code];
                AttributeMetadata updatedAttribute = updated[code];

                if (originalAttribute.TypeName != updatedAttribute.TypeName)
                    return true;
            }

            return false;
        }

        private HttpStatusCode GetReturnCode(ReturnStatementSyntax resultMetadata)
        {
            InvocationExpressionSyntax method = resultMetadata.Expression as InvocationExpressionSyntax;
            if (method != null)
            {
                string methodName = (method.Expression as IdentifierNameSyntax)?.Identifier.ValueText;
                switch (methodName)
                {
                    case "Ok":
                        return HttpStatusCode.OK;
                    case "Content":
                    case "StatusCode":
                        HttpStatusCode status;
                        if (!Enum.TryParse(
                            method.ArgumentList.Arguments.First().Expression.GetName().Identifier.ValueText,
                            out status))
                        {
                            throw new NotImplementedException();
                        }
                        return status;
                    case "NotFound":
                        return HttpStatusCode.NotFound;
                    case "BadRequest":
                        return HttpStatusCode.BadRequest;
                    case "Conflict":
                        return HttpStatusCode.Conflict;
                    case "Created":
                        return HttpStatusCode.Created;
                    case "InternalServerError":
                        return HttpStatusCode.InternalServerError;
                    case "Redirect":
                        return HttpStatusCode.Redirect;
                    case "Unauthorized":
                        return HttpStatusCode.Unauthorized;

                    default:
                        throw new ArgumentOutOfRangeException(nameof(methodName), $"The {methodName} return is not supported");
                }
            }

            return HttpStatusCode.OK;
        }

        private SyntaxList<AttributeListSyntax> DoRefactor(Dictionary<HttpStatusCode, AttributeMetadata> updatedList,
            MethodDeclarationSyntax method)
        {
            /* These are the codes that will be added after updating the existing ones.
             * They MUST be set to false from this Dictionary as the existing attributes
             * are updated. */
            Dictionary<HttpStatusCode, bool> statusCodesToAdd = updatedList.ToDictionary(k => k.Key, v => true);
            /* First, update existing */
            SyntaxList<AttributeListSyntax> clonedAttributes = method.AttributeLists;
            for (int index = 0; index < clonedAttributes.Count; index++)
            {
                AttributeListSyntax att = clonedAttributes[index];
                if (att.GetName() != AttributeName)
                    continue;
                List<AttributeArgumentSyntax> arguments = GetAttributeArguments(att).ToList();

                HttpStatusCode code;
                if (!GetStatusCode(arguments[0], out code))
                {
                    throw new NotImplementedException();
                }
                AttributeMetadata newAttribute = updatedList[code];

                /* Create type definition */
                AttributeArgumentSyntax typeArgument = null;
                if (newAttribute.TypeName != null)
                {
                    typeArgument = SyntaxFactory.AttributeArgument(null, null,
                        SyntaxFactory.TypeOfExpression(SyntaxFactory.ParseName(newAttribute.TypeName)));
                }
                SeparatedSyntaxList<AttributeArgumentSyntax> args =
                    new SeparatedSyntaxList<AttributeArgumentSyntax>();
                args = args.Add(arguments[0]);
                args = args.Add(arguments[1]);
                if (typeArgument != null)
                {
                    args = args.Add(typeArgument);
                }

                /* Compile arguments ito a new attribute */
                SeparatedSyntaxList<AttributeSyntax> attributeWhole = CreateAttribute(args);

                clonedAttributes = clonedAttributes.Replace(att, SyntaxFactory.AttributeList(att.OpenBracketToken,
                    att.Target, attributeWhole, att.CloseBracketToken));

                /* Update statusCodesToAdd */
                statusCodesToAdd[code] = false;
            }

            /* Return here if there are no attributes to add */
            if (!statusCodesToAdd.Any(v => v.Value))
                return clonedAttributes;

            /* Add new attributes */
            IEnumerable<AttributeMetadata> attributes =
                updatedList.Where(kv => statusCodesToAdd[kv.Key]).Select(a => a.Value);
            foreach (AttributeMetadata attributeToAdd in attributes)
            {
                SeparatedSyntaxList<AttributeArgumentSyntax> args =
                    new SeparatedSyntaxList<AttributeArgumentSyntax>();
                /* Add status code */
                args = args.Add(SyntaxFactory.AttributeArgument(null, null,
                    SyntaxFactory.ParseName($"{StatusCodeEnumName}.{attributeToAdd.StatusCode}")));
                /* Add default description */
                args = args.Add(SyntaxFactory.AttributeArgument(null, null,
                    SyntaxFactory.ParseName($"\"{attributeToAdd.Description}\"")));
                /* And, if applicable, add type */
                if (attributeToAdd.TypeName != null)
                    args = args.Add(SyntaxFactory.AttributeArgument(null, null,
                        SyntaxFactory.TypeOfExpression(SyntaxFactory.ParseName(attributeToAdd.TypeName))));

                AttributeListSyntax attribute =
                    SyntaxFactory.AttributeList(SyntaxFactory.Token(SyntaxKind.OpenBracketToken),
                        null, CreateAttribute(args), SyntaxFactory.Token(SyntaxKind.CloseBracketToken));
                clonedAttributes = clonedAttributes.Add(attribute);
            }

            return clonedAttributes;
        }

        private SeparatedSyntaxList<AttributeSyntax> CreateAttribute(SeparatedSyntaxList<AttributeArgumentSyntax> arguments)
        {
            AttributeArgumentListSyntax newArguments = SyntaxFactory.AttributeArgumentList(SyntaxFactory.Token(SyntaxKind.OpenParenToken),
                arguments, SyntaxFactory.Token(SyntaxKind.CloseParenToken));
            SeparatedSyntaxList<AttributeSyntax> attributeWhole = new SeparatedSyntaxList<AttributeSyntax>();
            attributeWhole = attributeWhole.Add(
                SyntaxFactory.Attribute(SyntaxFactory.ParseName(AttributeName), newArguments));

            return attributeWhole;
        }

        private string GetReturnTypeName(ReturnStatementSyntax syntax)
        {
            ObjectCreationExpressionSyntax obj = syntax.DescendantNodes().OfType<ObjectCreationExpressionSyntax>().FirstOrDefault();
            if (obj == null)
                return null;
            SyntaxToken? identifier = (obj.Type as IdentifierNameSyntax)?.Identifier;
            if (identifier.HasValue == false)
                return (obj.Type as QualifiedNameSyntax)?.ToString();
            return identifier?.ValueText;
        }

        private Dictionary<HttpStatusCode, AttributeMetadata> GetAttributeSummaries(IEnumerable<AttributeListSyntax> attributes)
        {
            Dictionary<HttpStatusCode, AttributeMetadata> returnTypes = new Dictionary<HttpStatusCode, AttributeMetadata>();
            foreach (AttributeListSyntax attributeListSyntax in attributes)
            {
                /* Add existing types */
                List<AttributeArgumentSyntax> arguments =
                    GetAttributeArguments(attributeListSyntax).ToList();

                HttpStatusCode statusCode;
                if (!GetStatusCode(arguments[0], out statusCode))
                {
                    /* TODO: fail here for argument not being a valid status code */
                }
                string desc = string.Empty;

                string type;
                if (arguments.Count >= 3)
                {
                    SyntaxToken? identifier = ((arguments[2].Expression as TypeOfExpressionSyntax)?.Type as IdentifierNameSyntax)
                        ?.Identifier;
                    type = identifier?.ValueText;
                }
                else
                    type = null;


                returnTypes.Add(statusCode, new AttributeMetadata(statusCode, desc, type));
            }

            return returnTypes;
        }

        private IEnumerable<AttributeArgumentSyntax> GetAttributeArguments(
            AttributeListSyntax attribute)
        {
            return attribute.DescendantNodes().OfType<AttributeArgumentSyntax>();
        }

        private bool GetStatusCode(AttributeArgumentSyntax attribute, out HttpStatusCode statusCode)
        {
            return Enum.TryParse(attribute.Expression.GetName().Identifier.ValueText, out statusCode);
        }

        private class AttributeMetadata
        {
            public HttpStatusCode StatusCode { get; set; }
            public string Description { get; set; }
            public string TypeName { get; set; }

            public AttributeMetadata(HttpStatusCode code, string desc, string type)
            {
                StatusCode = code;
                Description = desc;
                TypeName = type;
            }
        }
    }
}