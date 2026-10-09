using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Jellyfin.Plugin.Database.Npgsql;

/// <summary>
/// Rewrites the public LINQ shapes emitted by an unchanged Jellyfin host into
/// PostgreSQL expressions with Jellyfin's SQLite-compatible semantics.
/// </summary>
public sealed class NpgsqlQueryExpressionInterceptor : IQueryExpressionInterceptor
{
    private static readonly MethodInfo EfPropertyMethod = typeof(EF)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(method => method.Name == nameof(EF.Property) && method.IsGenericMethodDefinition);

    private static readonly MethodInfo AsciiLowerMethod = typeof(NpgsqlQueryDbFunctions)
        .GetMethod(nameof(NpgsqlQueryDbFunctions.AsciiLower), BindingFlags.Public | BindingFlags.Static)!;

    private static readonly MethodInfo AsciiPrefixMethod = typeof(NpgsqlQueryDbFunctions)
        .GetMethod(nameof(NpgsqlQueryDbFunctions.AsciiPrefix), BindingFlags.Public | BindingFlags.Static)!;

    private static readonly MethodInfo SqliteLikeMethod = typeof(NpgsqlQueryDbFunctions)
        .GetMethod(
            nameof(NpgsqlQueryDbFunctions.SqliteLike),
            BindingFlags.Public | BindingFlags.Static,
            [typeof(string), typeof(string)])!;

    private static readonly MethodInfo SqliteLikeWithEscapeMethod = typeof(NpgsqlQueryDbFunctions)
        .GetMethod(nameof(NpgsqlQueryDbFunctions.SqliteLikeWithEscape), BindingFlags.Public | BindingFlags.Static)!;

    private static readonly MethodInfo EfLikeWithEscapeMethod = typeof(DbFunctionsExtensions)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(method => method.Name == nameof(DbFunctionsExtensions.Like)
            && method.GetParameters().Length == 4);

    private static readonly MethodInfo StringConcatMethod = typeof(string)
        .GetMethod(nameof(string.Concat), BindingFlags.Public | BindingFlags.Static, [typeof(string), typeof(string)])!;

    /// <summary>Gets the stateless shared interceptor.</summary>
    public static NpgsqlQueryExpressionInterceptor Instance { get; } = new();

    /// <inheritdoc />
    public Expression QueryCompilationStarting(Expression queryExpression, QueryExpressionEventData eventData)
    {
        ArgumentNullException.ThrowIfNull(queryExpression);
        return new CompatibilityVisitor().Visit(queryExpression);
    }

    private sealed class CompatibilityVisitor : ExpressionVisitor
    {
        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            var visited = (MethodCallExpression)base.VisitMethodCall(node);

            if (IsEfLike(visited.Method))
            {
                return RewriteLike(visited);
            }

            if (IsPeopleNameStartsWith(visited))
            {
                return RewritePeopleNameStartsWith(visited);
            }

            if (visited.Method.DeclaringType == typeof(Queryable)
                && visited.Method.IsGenericMethod
                && visited.Arguments.Count == 2
                && visited.Method.Name is nameof(Queryable.OrderBy)
                    or nameof(Queryable.OrderByDescending)
                    or nameof(Queryable.ThenBy)
                    or nameof(Queryable.ThenByDescending))
            {
                return RewriteNullableOrdering(visited);
            }

            return visited;
        }

        private static bool IsEfLike(MethodInfo method)
            => method.DeclaringType == typeof(DbFunctionsExtensions)
                && method.Name == nameof(DbFunctionsExtensions.Like)
                && method.GetParameters().Length is 3 or 4;

        private static MethodCallExpression RewriteLike(MethodCallExpression node)
        {
            return node.Arguments.Count == 4
                ? Expression.Call(
                    SqliteLikeWithEscapeMethod,
                    node.Arguments[1],
                    node.Arguments[2],
                    node.Arguments[3])
                : Expression.Call(
                    SqliteLikeMethod,
                    node.Arguments[1],
                    node.Arguments[2]);
        }

        private static bool IsPeopleNameStartsWith(MethodCallExpression node)
        {
            if (node.Object is not MemberExpression member)
            {
                return false;
            }

            return node.Method.DeclaringType == typeof(string)
                && node.Method.Name == nameof(string.StartsWith)
                && node.Arguments.Count == 1
                && member.Member.Name == nameof(Jellyfin.Database.Implementations.Entities.People.Name)
                && member.Expression?.Type == typeof(Jellyfin.Database.Implementations.Entities.People);
        }

        private static BinaryExpression RewritePeopleNameStartsWith(MethodCallExpression node)
        {
            var member = (MemberExpression)node.Object!;
            var loweredName = Expression.Call(
                EfPropertyMethod.MakeGenericMethod(typeof(string)),
                member.Expression!,
                Expression.Constant("NameLower"));
            var loweredPrefix = Expression.Call(AsciiLowerMethod, node.Arguments[0]);
            var boundedName = Expression.Call(
                EfPropertyMethod.MakeGenericMethod(typeof(string)),
                member.Expression!,
                Expression.Constant("NameLowerPrefix"));
            var boundedPrefix = Expression.Call(AsciiPrefixMethod, node.Arguments[0]);
            return Expression.AndAlso(
                Expression.Call(
                    EfLikeWithEscapeMethod,
                    Expression.Constant(EF.Functions),
                    boundedName,
                    Expression.Call(StringConcatMethod, boundedPrefix, Expression.Constant("%")),
                    Expression.Constant(string.Empty)),
                Expression.Call(loweredName, node.Method, loweredPrefix));
        }

        private static MethodCallExpression RewriteNullableOrdering(MethodCallExpression node)
        {
            var lambda = (LambdaExpression)StripQuote(node.Arguments[1]);
            if (lambda.ReturnType.IsValueType && Nullable.GetUnderlyingType(lambda.ReturnType) is null)
            {
                return node;
            }

            var isDescending = node.Method.Name is nameof(Queryable.OrderByDescending)
                or nameof(Queryable.ThenByDescending);
            var isThenBy = node.Method.Name is nameof(Queryable.ThenBy)
                or nameof(Queryable.ThenByDescending);
            var nullValue = isDescending ? 1 : 0;
            var nonNullValue = isDescending ? 0 : 1;
            var markerBody = Expression.Condition(
                Expression.Equal(lambda.Body, Expression.Constant(null, lambda.ReturnType)),
                Expression.Constant(nullValue),
                Expression.Constant(nonNullValue));
            var marker = Expression.Lambda(markerBody, lambda.Parameters);

            var sourceType = node.Method.GetGenericArguments()[0];
            var markerMethod = QueryableMethod(
                isThenBy ? nameof(Queryable.ThenBy) : nameof(Queryable.OrderBy),
                sourceType,
                typeof(int));
            var markedSource = Expression.Call(markerMethod, node.Arguments[0], Expression.Quote(marker));

            var valueMethod = QueryableMethod(
                isDescending ? nameof(Queryable.ThenByDescending) : nameof(Queryable.ThenBy),
                sourceType,
                lambda.ReturnType);
            return Expression.Call(valueMethod, markedSource, Expression.Quote(lambda));
        }

        private static MethodInfo QueryableMethod(string name, Type sourceType, Type keyType)
            => typeof(Queryable)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Single(method => method.Name == name
                    && method.IsGenericMethodDefinition
                    && method.GetGenericArguments().Length == 2
                    && method.GetParameters().Length == 2)
                .MakeGenericMethod(sourceType, keyType);

        private static Expression StripQuote(Expression expression)
        {
            while (expression.NodeType == ExpressionType.Quote)
            {
                expression = ((UnaryExpression)expression).Operand;
            }

            return expression;
        }
    }
}
