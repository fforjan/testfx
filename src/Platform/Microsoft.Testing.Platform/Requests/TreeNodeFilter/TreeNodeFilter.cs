﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Text;
using System.Text.RegularExpressions;

using Microsoft.Testing.Platform.Extensions.Messages;
using Microsoft.Testing.Platform.Helpers;

namespace Microsoft.Testing.Platform.Requests;

internal sealed class TreeNodeFilter : ITestExecutionFilter
{
    public const char PathSeparator = '/';
    internal const char PropertyPerEdgeStartChar = '[';
    internal const char PropertyPerEdgeEndChar = ']';

    // Note: After the token gets expanded into regex ** gets converted to .*.*.
    internal const string AllNodesBelowRegexString = ".*.*";
    private readonly List<FilterExpression> _filters;

    internal TreeNodeFilter(string filter)
    {
        ArgumentGuard.IsNotNull(filter);
        Filter = filter;
        _filters = ParseFilter(filter);
    }

    public string Filter { get; }

    /// <remarks>
    /// The current grammar for the filter looks as follows:
    /// <code>
    /// TREE_NODE_FILTER = EXPR ( '/' EXPR )*
    /// EXPR =
    ///   '(' EXPR ')'
    ///   | EXPR OP EXPR
    ///   | NODE_VALUE
    /// FILTER_EXPR =
    ///   '(' FILTER_EXPR ')'
    ///   | TOKEN '=' TOKEN
    ///   | FILTER_EXPR OP FILTER_EXPR
    ///   | TOKEN
    /// OP = '&amp;' | '|'
    /// NODE_VALUE = TOKEN | TOKEN '[' FILTER_EXPR ']'
    /// TOKEN = string
    /// </code>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Exception thrown, if the filter is malformed, for example <c>A(|B)</c> or <c>A)</c>.
    /// </exception>
    private static List<FilterExpression> ParseFilter(string filter)
    {
        // This parsing is an implementation of Dijkstra's shunting yard algorithm.
        //
        // Parsing works by creating two stacks, the expression stack and the operator stack.
        // For instance when parsing an expression A | B, first A is put on the expression stack,
        // | is placed on the operator stack and B is placed on the expression stack.
        // Finally the filter expressions are generated by applying the operators on the stack to
        // the top most expressions, until the operator stack is empty.
        //
        // Note: We do some additional state tracking to parse just the valid expressions.
        //       For instance we should not allow multiple parameter values like A[P1=1][P2=2]
        //       or allow nested parameters A[P1=B[P2=C]]
        Stack<FilterExpression> expressionStack = new();
        Stack<OperatorKind> operatorStack = new();

        // Note: Whether or not we're at a place where operator is allowed within an expression.
        //       For instance right after an open parenthesis, after an operator or at the beginning
        //       of an expression operators are not allowed.
        bool isOperatorAllowed = false;
        bool isPropAllowed = false;

        OperatorKind topStackOperator;

        foreach (string token in TokenizeFilter(filter))
        {
            switch (token)
            {
                case "&":
                case "|":
                    if (!isOperatorAllowed)
                    {
                        throw new InvalidOperationException();
                    }

                    OperatorKind currentOp = token switch
                    {
                        "&" => OperatorKind.And,
                        "|" => OperatorKind.Or,
                        _ => throw new UnreachableException(),
                    };

                    ProcessHigherPrecedenceOperators(expressionStack, operatorStack, currentOp);

                    isOperatorAllowed = false;
                    isPropAllowed = false;
                    break;

                case "/":
                    ProcessHigherPrecedenceOperators(expressionStack, operatorStack, OperatorKind.Separator);

                    isOperatorAllowed = false;
                    isPropAllowed = false;
                    break;

                case "(":
                    // We should consider pushing a well known expression
                    // such as a NopExpression onto the filter operation stack.
                    // This way it's possible to see in the filter stack the boundaries between the different
                    // operations.
                    // For instance we should be able to distinguish A(&B) vs (A&B), but
                    // if we don't separate subexpression stacks, we won't see
                    operatorStack.Push(OperatorKind.LeftBrace);

                    isOperatorAllowed = false;
                    isPropAllowed = false;
                    break;

                case ")":
                    // Note: If operator is not allowed, we must be at the start of an expression
                    //       for example "(". In that case, it's not valid to complete the statement.
                    if (!isOperatorAllowed)
                    {
                        throw new InvalidOperationException();
                    }

                    // Note: Keep merging the operator stack, until the matching brace.
                    while (true)
                    {
                        if (operatorStack.Count == 0)
                        {
                            // Reaching this implies that the input string is unbalanced.
                            // For instance ")".
                            throw new InvalidOperationException("Invalid input filter string.");
                        }

                        topStackOperator = operatorStack.Pop();
                        if (topStackOperator == OperatorKind.LeftBrace)
                        {
                            break;
                        }

                        ProcessStackOperator(topStackOperator, expressionStack, operatorStack);
                    }

                    isOperatorAllowed = true;
                    isPropAllowed = false;

                    break;

                case "]":
                    // Note: If operator is not allowed, we must be at the start of an expression
                    //       for example "[". In that case, it's not valid to complete the statement.
                    if (!isOperatorAllowed)
                    {
                        throw new InvalidOperationException();
                    }

                    // Note: Keep merging the operator stack, until the matching parameter.
                    while (true)
                    {
                        if (operatorStack.Count == 0)
                        {
                            // Reaching this implies that the input string is unbalanced.
                            // For instance "]".
                            throw new InvalidOperationException("Invalid input filter string.");
                        }

                        topStackOperator = operatorStack.Pop();
                        if (topStackOperator == OperatorKind.LeftParameter)
                        {
                            break;
                        }

                        ProcessStackOperator(topStackOperator, expressionStack, operatorStack);
                    }

                    // We should end up with an expression and a property.
                    FilterExpression propExpr = expressionStack.Pop();
                    FilterExpression nodeExpr = expressionStack.Pop();

                    if (nodeExpr is not ValueExpression nodeValueExpr)
                    {
                        throw new InvalidOperationException();
                    }

                    expressionStack.Push(new ValueAndPropertyExpression(nodeValueExpr, propExpr));

                    isPropAllowed = false;
                    isOperatorAllowed = true;

                    break;

                case "[":
                    if (!isPropAllowed)
                    {
                        throw new InvalidOperationException();
                    }

                    operatorStack.Push(OperatorKind.LeftParameter);

                    isOperatorAllowed = false;
                    isPropAllowed = false;
                    break;

                case "=":
                    operatorStack.Push(OperatorKind.FilterEquals);

                    isOperatorAllowed = false;
                    isPropAllowed = false;
                    break;

                default:
                    expressionStack.Push(new ValueExpression(token));

                    isOperatorAllowed = true;
                    isPropAllowed = true;
                    break;
            }
        }

        // Note: What we should end with (as long as the expression is a valid filter)
        // is a bunch of processed separator operators followed by a potentially unprocessed expression.
        while (operatorStack.Count > 0 && operatorStack.Peek() != OperatorKind.Separator)
        {
            topStackOperator = operatorStack.Pop();
            ProcessStackOperator(topStackOperator, expressionStack, operatorStack);
        }

        var parsedFilter = expressionStack.Reverse().ToList();

        for (int i = 0; i < parsedFilter.Count; i++)
        {
            ValidateExpression(parsedFilter[i], isMatchAllAllowed: i == parsedFilter.Count - 1);
        }

        return parsedFilter;

        // Note: By default as the stack is popped, the operators are
        // processed in order, so A B C, Op1 Op2
        // will be processed as A Op1 (B Op2 C)
        // In cases where the Op1 has a higher priority the natural parsing would
        // result in (A Op1 B) Op2 C. As such, we need to process higher priority
        // operators before pushing lower priority operators on the stack.
        static void ProcessHigherPrecedenceOperators(
            Stack<FilterExpression> expressionStack,
            Stack<OperatorKind> operatorStack,
            OperatorKind currentOp)
        {
            OperatorKind topStackOperator;

            while (operatorStack.Count != 0 && operatorStack.Peek() > currentOp)
            {
                topStackOperator = operatorStack.Pop();
                ProcessStackOperator(topStackOperator, expressionStack, operatorStack);
                break;
            }

            operatorStack.Push(currentOp);
        }
    }

    private static void ValidateExpression(FilterExpression expr, bool isMatchAllAllowed)
    {
        switch (expr)
        {
            case OperatorExpression { Op: FilterOperator.Not, SubExpressions: var subexprsNot } when subexprsNot.Count != 1:
            case OperatorExpression { Op: FilterOperator.And, SubExpressions: var subexprsAnd } when subexprsAnd.Count < 2:
            case OperatorExpression { Op: FilterOperator.Or, SubExpressions: var subexprsOr } when subexprsOr.Count < 2:
                throw new UnreachableException();

            case OperatorExpression opExpr:
                foreach (FilterExpression childExpr in opExpr.SubExpressions)
                {
                    ValidateExpression(childExpr, isMatchAllAllowed);
                }

                break;

            case ValueExpression vExpr when vExpr.Value.Contains(PathSeparator):
                throw new InvalidOperationException($"""A filter "{vExpr.Value}" should not contain a / character.""");

            case ValueExpression vExpr when vExpr.Value.Equals(AllNodesBelowRegexString, StringComparison.Ordinal) && !isMatchAllAllowed:
                throw new InvalidOperationException("Only the final filter path can contain ** wildcard.");
        }
    }

    private static void ProcessStackOperator(OperatorKind op, Stack<FilterExpression> expr, Stack<OperatorKind> ops)
    {
        switch (op)
        {
            case OperatorKind.And:
            case OperatorKind.Or:
                var subexprs = new List<FilterExpression>
                {
                    expr.Pop(),
                    expr.Pop(),
                };

                // Note: An OR/AND operator allow to pass it in a list of expressions.
                // We can keep popping following operators and add them to the collection,
                // so that A | B | C is represented as OR { A, B, C }.
                // This limits the recursion needed to evaluate the expressions down the line.
                while (ops.Count > 0 && ops.Peek() == op)
                {
                    ops.Pop();
                    subexprs.Add(expr.Pop());
                }

                FilterOperator filter = op switch
                {
                    OperatorKind.And => FilterOperator.And,
                    OperatorKind.Or => FilterOperator.Or,
                    OperatorKind.FilterEquals => FilterOperator.Equals,
                    _ => throw new UnreachableException(),
                };

                expr.Push(new OperatorExpression(filter, subexprs));
                break;

            case OperatorKind.FilterEquals:
                FilterExpression valueExpr = expr.Pop();
                FilterExpression propExpr = expr.Pop();

                if (propExpr is not ValueExpression propValueExpr ||
                    valueExpr is not ValueExpression valueValueExpr)
                {
                    throw new InvalidOperationException();
                }

                expr.Push(new PropertyExpression(propValueExpr, valueValueExpr));
                break;

            default:
                // Note: Handling of other operations in valid scenarios should be handled by the caller.
                //       Reaching this code for instance means that we're trying to process / operator
                //       in the middle of a ( expression ).
                throw new InvalidOperationException("Invalid input filter string.");
        }
    }

    private static IEnumerable<string> TokenizeFilter(string filter)
    {
        int i = 0;
        var lastStringTokenBuilder = new StringBuilder();
        int openedSquareBrackets = 0;

        while (i < filter.Length)
        {
            switch (filter[i])
            {
                case '\\':
                    if (i + 1 < filter.Length)
                    {
                        // Note: In case of an escape sequence take the next character and
                        //       add to the token in an escaped form. This is to encode [ as \[
                        //       so that regex will parse it directly.
                        lastStringTokenBuilder.Append(Regex.Escape(filter[i + 1].ToString()));

                        // Note: Skip the next character.
                        i++;
                    }
                    else
                    {
                        // Note: An escape character should not terminate a filter string.
                        throw new InvalidOperationException("An escape character should not terminate the filter string");
                    }

                    break;

                case '*':
                    lastStringTokenBuilder.Append(".*");
                    break;

                case '[':
                    openedSquareBrackets++;
                    goto case '=';

                case ']':
                    openedSquareBrackets--;
                    goto case '=';

                case '/':
                    if (openedSquareBrackets > 0)
                    {
                        lastStringTokenBuilder.Append(filter[i]);
                    }
                    else
                    {
                        goto case '=';
                    }

                    break;

                case '=':
                case '(':
                case ')':
                case '|':
                case '&':
                    if (lastStringTokenBuilder.Length > 0)
                    {
                        yield return lastStringTokenBuilder.ToString();
                        lastStringTokenBuilder.Clear();
                    }

                    yield return filter[i].ToString();

                    break;

                default:
                    lastStringTokenBuilder.Append(Regex.Escape(filter[i].ToString()));
                    break;
            }

            i++;
        }

        if (lastStringTokenBuilder.Length > 0)
        {
            yield return lastStringTokenBuilder.ToString();
        }
    }

    /// <summary>
    /// Checks whether a node path matches the tree node filter.
    /// </summary>
    /// <param name="testNodeFullPath">The segment URL encoded path.</param>
    /// <param name="filterableProperties">The URL encoded node properties.</param>
    public bool MatchesFilter(string testNodeFullPath, PropertyBag filterableProperties)
    {
        ArgumentGuard.IsNotNullOrEmpty(testNodeFullPath);
        ArgumentGuard.Ensure(testNodeFullPath[0] == PathSeparator, nameof(testNodeFullPath),
            $"Invalid node path, expected root as first character '{PathSeparator}'");

        int currentCharIndex = 1;
        int currentFragmentIndex = 0;
        while (true)
        {
            int nextFragmentStartIndex = testNodeFullPath.IndexOf(PathSeparator, currentCharIndex);

            if (currentFragmentIndex >= _filters.Count)
            {
                // Note: The regex for ** is .*.*, so we match against such a value expression.
                return currentFragmentIndex > 0 && _filters.Last() is ValueExpression { Value: ".*.*" };
            }

            if (!MatchFilterPattern(
                    _filters[currentFragmentIndex],
                    testNodeFullPath,
                    currentCharIndex,
                    nextFragmentStartIndex == -1 ? testNodeFullPath.Length : nextFragmentStartIndex,
                    filterableProperties))
            {
                return false;
            }

            currentFragmentIndex++;

            if (nextFragmentStartIndex < 0)
            {
                break;
            }

            currentCharIndex = nextFragmentStartIndex + 1;
        }

        return true;
    }

    private static bool MatchFilterPattern(
        FilterExpression filterExpression,
        string testNodeFullPath,
        int startFragmentIndex,
        int endFragmentIndex,
        PropertyBag properties)
    {
        string str = testNodeFullPath[startFragmentIndex..endFragmentIndex];
        return MatchFilterPattern(filterExpression, str, properties);
    }

    private static bool MatchFilterPattern(
        FilterExpression filterExpression,
        string testNodeFragment,
        PropertyBag properties)
        => filterExpression switch
        {
            ValueExpression vExpr => vExpr.Regex.IsMatch(testNodeFragment),
            OperatorExpression { Op: FilterOperator.Or, SubExpressions: var subexprs }
                => subexprs.Any(expr => MatchFilterPattern(expr, testNodeFragment, properties)),
            OperatorExpression { Op: FilterOperator.And, SubExpressions: var subexprs }
                => subexprs.All(expr => MatchFilterPattern(expr, testNodeFragment, properties)),
            OperatorExpression { Op: FilterOperator.Not, SubExpressions: var subexprs }
                => !MatchFilterPattern(subexprs.Single(), testNodeFragment, properties),
            ValueAndPropertyExpression { Value: var valueExpr, Properties: var propExpr }
                => MatchFilterPattern(valueExpr, testNodeFragment, properties)
                    && MatchProperties(propExpr, properties),
            NopExpression => true,
            _ => throw new NotSupportedException(),
        };

    private static bool MatchProperties(
        FilterExpression propertyExpr,
        PropertyBag properties)
        => propertyExpr switch
        {
            PropertyExpression { PropertyName: var propExpr, Value: var valueExpr }
                => properties.AsEnumerable().Any(prop => prop is KeyValuePairStringProperty kvpProperty && propExpr.Regex.IsMatch(kvpProperty.Key) && valueExpr.Regex.IsMatch(kvpProperty.Value)),
            OperatorExpression { Op: FilterOperator.Or, SubExpressions: var subExprs }
                => subExprs.Any(expr => MatchProperties(expr, properties)),
            OperatorExpression { Op: FilterOperator.And, SubExpressions: var subExprs }
                => subExprs.All(expr => MatchProperties(expr, properties)),
            OperatorExpression { Op: FilterOperator.Not, SubExpressions: var subExprs }
                => !MatchProperties(subExprs.Single(), properties),
            _ => throw new NotSupportedException(),
        };
}
