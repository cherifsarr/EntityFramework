// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Query.Expressions;
using Microsoft.EntityFrameworkCore.Utilities;

namespace Microsoft.EntityFrameworkCore.Query.ExpressionTranslators
{
    /// <summary>
    ///     A translator for <see cref="string.IsNullOrEmpty"/>.
    /// </summary>
    public class IsNullOrEmptyTranslator : IMethodCallTranslator
    {
        private static readonly MethodInfo _methodInfo
            = typeof(string).GetRuntimeMethod(nameof(string.IsNullOrEmpty), new[] { typeof(string) });

        /// <summary>
        ///     Translates the given method call expression.
        /// </summary>
        /// <param name="methodCallExpression"> The method call expression. </param>
        /// <returns>
        ///     An Expression.
        /// </returns>
        public virtual Expression Translate(MethodCallExpression methodCallExpression)
        {
            Check.NotNull(methodCallExpression, nameof(methodCallExpression));

            return ReferenceEquals(methodCallExpression.Method, _methodInfo)
                ? Expression.MakeBinary(
                    ExpressionType.OrElse,
                    new IsNullExpression(methodCallExpression.Arguments[0]),
                    Expression.Equal(methodCallExpression.Arguments[0], Expression.Constant("", typeof(string))))
                : null;
        }
    }
}
