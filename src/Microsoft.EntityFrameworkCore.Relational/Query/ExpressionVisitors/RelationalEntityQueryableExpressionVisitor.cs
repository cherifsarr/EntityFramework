// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore.Internal;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Internal;
using Microsoft.EntityFrameworkCore.Query.Expressions;
using Microsoft.EntityFrameworkCore.Query.ExpressionVisitors.Internal;
using Microsoft.EntityFrameworkCore.Query.Internal;
using Microsoft.EntityFrameworkCore.Query.ResultOperators.Internal;
using Microsoft.EntityFrameworkCore.Query.Sql;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Utilities;
using Remotion.Linq.Clauses;
using Remotion.Linq.Clauses.Expressions;

namespace Microsoft.EntityFrameworkCore.Query.ExpressionVisitors
{
    public class RelationalEntityQueryableExpressionVisitor : EntityQueryableExpressionVisitor
    {
        private readonly IModel _model;
        private readonly ISelectExpressionFactory _selectExpressionFactory;
        private readonly IMaterializerFactory _materializerFactory;
        private readonly IShaperCommandContextFactory _shaperCommandContextFactory;
        private readonly IRelationalAnnotationProvider _relationalAnnotationProvider;
        private readonly IQuerySource _querySource;

        public RelationalEntityQueryableExpressionVisitor(
            [NotNull] IModel model,
            [NotNull] ISelectExpressionFactory selectExpressionFactory,
            [NotNull] IMaterializerFactory materializerFactory,
            [NotNull] IShaperCommandContextFactory shaperCommandContextFactory,
            [NotNull] IRelationalAnnotationProvider relationalAnnotationProvider,
            [NotNull] RelationalQueryModelVisitor queryModelVisitor,
            [CanBeNull] IQuerySource querySource)
            : base(Check.NotNull(queryModelVisitor, nameof(queryModelVisitor)))
        {
            Check.NotNull(model, nameof(model));
            Check.NotNull(selectExpressionFactory, nameof(selectExpressionFactory));
            Check.NotNull(materializerFactory, nameof(materializerFactory));
            Check.NotNull(shaperCommandContextFactory, nameof(shaperCommandContextFactory));
            Check.NotNull(relationalAnnotationProvider, nameof(relationalAnnotationProvider));

            _model = model;
            _selectExpressionFactory = selectExpressionFactory;
            _materializerFactory = materializerFactory;
            _shaperCommandContextFactory = shaperCommandContextFactory;
            _relationalAnnotationProvider = relationalAnnotationProvider;
            _querySource = querySource;
        }

        private new RelationalQueryModelVisitor QueryModelVisitor => (RelationalQueryModelVisitor)base.QueryModelVisitor;

        protected override Expression VisitSubQuery(SubQueryExpression expression)
        {
            Check.NotNull(expression, nameof(expression));

            var queryModelVisitor = (RelationalQueryModelVisitor)CreateQueryModelVisitor();

            queryModelVisitor.VisitQueryModel(expression.QueryModel);

            if (_querySource != null)
            {
                QueryModelVisitor.RegisterSubQueryVisitor(_querySource, queryModelVisitor);
            }

            return queryModelVisitor.Expression;
        }

        protected override Expression VisitMember(MemberExpression node)
        {
            Check.NotNull(node, nameof(node));

            QueryModelVisitor
                .BindMemberExpression(
                    node,
                    (property, querySource, selectExpression)
                        => selectExpression.AddToProjection(
                            _relationalAnnotationProvider.For(property).ColumnName,
                            property,
                            querySource),
                    bindSubQueries: true);

            return base.VisitMember(node);
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            Check.NotNull(node, nameof(node));

            QueryModelVisitor
                .BindMethodCallExpression(
                    node,
                    (property, querySource, selectExpression)
                        => selectExpression.AddToProjection(
                            _relationalAnnotationProvider.For(property).ColumnName,
                            property,
                            querySource),
                    bindSubQueries: true);

            return base.VisitMethodCall(node);
        }

        protected override Expression VisitEntityQueryable(Type elementType)
        {
            Check.NotNull(elementType, nameof(elementType));

            var relationalQueryCompilationContext = QueryModelVisitor.QueryCompilationContext;
            var entityType = _model.FindEntityType(elementType);

            var selectExpression = _selectExpressionFactory.Create(relationalQueryCompilationContext);

            QueryModelVisitor.AddQuery(_querySource, selectExpression);

            var name = _relationalAnnotationProvider.For(entityType).TableName;

            var tableAlias
                = _querySource.HasGeneratedItemName()
                    ? name[0].ToString().ToLowerInvariant()
                    : _querySource.ItemName;

            var fromSqlAnnotation
                = relationalQueryCompilationContext
                    .QueryAnnotations
                    .OfType<FromSqlResultOperator>()
                    .LastOrDefault(a => a.QuerySource == _querySource);

            Func<IQuerySqlGenerator> querySqlGeneratorFunc = selectExpression.CreateDefaultQuerySqlGenerator;

            if (fromSqlAnnotation == null)
            {
                selectExpression.AddTable(
                    new TableExpression(
                        name,
                        _relationalAnnotationProvider.For(entityType).Schema,
                        tableAlias,
                        _querySource));
            }
            else
            {
                selectExpression.AddTable(
                    new FromSqlExpression(
                        fromSqlAnnotation.Sql,
                        fromSqlAnnotation.Arguments,
                        tableAlias,
                        _querySource));

                var useQueryComposition
                    = fromSqlAnnotation.Sql
                        .TrimStart()
                        .StartsWith("SELECT ", StringComparison.OrdinalIgnoreCase);

                if (!useQueryComposition)
                {
                    if (relationalQueryCompilationContext.IsIncludeQuery)
                    {
                        throw new InvalidOperationException(
                            RelationalStrings.StoredProcedureIncludeNotSupported);
                    }
                }

                if (useQueryComposition
                    && fromSqlAnnotation.QueryModel.IsIdentityQuery()
                    && !fromSqlAnnotation.QueryModel.ResultOperators.Any()
                    && !relationalQueryCompilationContext.IsIncludeQuery)
                {
                    useQueryComposition = false;
                }

                if (!useQueryComposition)
                {
                    QueryModelVisitor.RequiresClientEval = true;

                    querySqlGeneratorFunc = ()
                        => selectExpression.CreateFromSqlQuerySqlGenerator(
                            fromSqlAnnotation.Sql,
                            fromSqlAnnotation.Arguments);
                }
            }

            var shaper = CreateShaper(elementType, entityType, selectExpression);

            return Expression.Call(
                QueryModelVisitor.QueryCompilationContext.QueryMethodProvider // TODO: Don't use ShapedQuery when projecting
                    .ShapedQueryMethod
                    .MakeGenericMethod(shaper.Type),
                EntityQueryModelVisitor.QueryContextParameter,
                Expression.Constant(_shaperCommandContextFactory.Create(querySqlGeneratorFunc)),
                Expression.Constant(shaper));
        }

        private Shaper CreateShaper(Type elementType, IEntityType entityType, SelectExpression selectExpression)
        {
            Shaper shaper;

            if (QueryModelVisitor.QueryCompilationContext
                .QuerySourceRequiresMaterialization(_querySource)
                || QueryModelVisitor.RequiresClientEval)
            {
                var materializer
                    = _materializerFactory
                        .CreateMaterializer(
                            entityType,
                            selectExpression,
                            (p, se) =>
                                se.AddToProjection(
                                    _relationalAnnotationProvider.For(p).ColumnName,
                                    p,
                                    _querySource),
                            _querySource).Compile();

                shaper
                    = (Shaper)_createEntityShaperMethodInfo.MakeGenericMethod(elementType)
                        .Invoke(null, new object[]
                        {
                            _querySource,
                            entityType.DisplayName(),
                            QueryModelVisitor.QueryCompilationContext.IsTrackingQuery,
                            entityType.FindPrimaryKey(),
                            materializer,
                            QueryModelVisitor.QueryCompilationContext.IsQueryBufferRequired
                        });
            }
            else
            {
                DiscriminateProjectionQuery(entityType, selectExpression, _querySource);

                shaper = new ValueBufferShaper(_querySource);
            }

            return shaper;
        }

        private void DiscriminateProjectionQuery(
            IEntityType entityType, SelectExpression selectExpression, IQuerySource querySource)
        {
            var concreteEntityTypes
                = entityType.GetConcreteTypesInHierarchy().ToArray();

            if (concreteEntityTypes.Length == 1
                && concreteEntityTypes[0].RootType() == concreteEntityTypes[0])
            {
                return;
            }

            var discriminatorProperty
                = _relationalAnnotationProvider
                    .For(concreteEntityTypes[0]).DiscriminatorProperty;

            var discriminatorColumn
                = new ColumnExpression(
                    _relationalAnnotationProvider.For(discriminatorProperty).ColumnName,
                    discriminatorProperty,
                    selectExpression.GetTableForQuerySource(querySource));

            var firstDiscriminatorValue
                = Expression.Constant(
                    _relationalAnnotationProvider.For(concreteEntityTypes[0]).DiscriminatorValue);

            var discriminatorPredicate
                = Expression.Equal(discriminatorColumn, firstDiscriminatorValue);

            if (concreteEntityTypes.Length == 1)
            {
                selectExpression.Predicate
                    = new DiscriminatorPredicateExpression(discriminatorPredicate, querySource);

                return;
            }

            discriminatorPredicate
                = concreteEntityTypes
                    .Skip(1)
                    .Select(concreteEntityType
                        => Expression.Constant(
                            _relationalAnnotationProvider
                                .For(concreteEntityType).DiscriminatorValue))
                    .Aggregate(discriminatorPredicate, (current, discriminatorValue) =>
                        Expression.OrElse(
                            Expression.Equal(discriminatorColumn, discriminatorValue),
                            current));

            selectExpression.Predicate
                = new DiscriminatorPredicateExpression(discriminatorPredicate, querySource);
        }

        private static readonly MethodInfo _createEntityShaperMethodInfo
            = typeof(RelationalEntityQueryableExpressionVisitor).GetTypeInfo()
                .GetDeclaredMethod(nameof(CreateEntityShaper));

        [UsedImplicitly]
        private static IShaper<TEntity> CreateEntityShaper<TEntity>(
            IQuerySource querySource,
            string entityType,
            bool trackingQuery,
            IKey key,
            Func<ValueBuffer, object> materializer,
            bool useQueryBuffer)
            where TEntity : class
            => !useQueryBuffer
                ? (IShaper<TEntity>)new UnbufferedEntityShaper<TEntity>(
                    querySource,
                    entityType,
                    trackingQuery,
                    key,
                    materializer)
                : new BufferedEntityShaper<TEntity>(
                    querySource,
                    entityType,
                    trackingQuery,
                    key,
                    materializer);
    }
}
