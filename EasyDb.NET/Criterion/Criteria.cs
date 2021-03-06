﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using LX.EasyDb.Dialects;
using LX.EasyDb.Dialects.Function;

namespace LX.EasyDb.Criterion
{
    class Criteria : ICriteria, ICriteriaRender
    {
        protected IConnection _connection;
        private IConnectionFactorySupport _factory;
        private List<IExpression> _conditions = new List<IExpression>();
        private IProjection _projection;
        private List<Order> _orders = new List<Order>();
        private Dictionary<String, Object> _params = new Dictionary<String, Object>();
        private readonly Mapping.Table _table;
        private readonly Type _type;
        private readonly String _entity;

        public Int32 Offset { get; set; }
        public Int32 Total { get; set; }

        public Criteria(Type type, IConnection connection, IConnectionFactorySupport factory)
            : this(factory.Mapping.FindTable(type), connection, factory)
        {
            _type = type;
        }

        public Criteria(String entity, IConnection connection, IConnectionFactorySupport factory)
            : this(factory.Mapping.FindTable(entity), connection, factory)
        {
            _entity = entity;
        }

        private Criteria(Mapping.Table table, IConnection connection, IConnectionFactorySupport factory)
        {
            _table = table;
            _connection = connection;
            _factory = factory;
            Parameterized = true;
            Total = -1;
            Offset = 0;
        }

        public Boolean Parameterized { get; set; }

        private String RegisterParam(String name, Object value)
        {
            _params[name] = value;
            return _factory.Dialect.ParamPrefix + name;
        }

        private String RegisterParam(Object value)
        {
            return RegisterParam("p_" + _params.Count, value);
        }

        public ICriteria SetProjection(IProjection projection)
        {
            _projection = projection;
            return this;
        }

        public ICriteria Add(IExpression condition)
        {
            _conditions.Add(condition);
            return this;
        }

        public ICriteria AddOrder(Order order)
        {
            _orders.Add(order);
            return this;
        }

        public IDictionary<String, Object> Parameters
        {
            get { return _params; }
        }

        public IEnumerable List()
        {
            return List(-1, 0);
        }

        public IEnumerable List(Int32 total = -1, Int32 offset = 0)
        {
            Total = total;
            Offset = offset;
            if (_type != null)
                return Enumerable.ToList(_connection.Query(_type, ToSqlString(), Parameters));
            else
                return Enumerable.ToList(_connection.Query(_entity, ToSqlString(), Parameters));
        }

        public Int32 Count()
        {
            return Enumerable.Single<Int32>(_connection.Query<Int32>(ToSqlCountString(), Parameters));
        }

        public Object SingleOrDefault()
        {
            return Enumerable.SingleOrDefault(List(1));
        }

        public String ToSqlString()
        {
            String orderby = GenerateOrder();
            String sql = GenerateSelect();
            if (Total >= 0)
                sql = _factory.Dialect.GetPaging(sql, orderby, Total, Offset);
            else if (orderby != null)
                sql += " " + orderby;
#if DEBUG
            //Console.WriteLine(sql);
#endif
            return sql;
        }

        public String ToSqlCountString()
        {
            String select = GenerateSelect();
            StringBuilder sbSql = StringHelper.CreateBuilder()
                 .Append("SELECT COUNT(*) FROM (")
                 .Append(select)
                 .Append(") t");
            return sbSql.ToString();
        }

        private String GenerateSelect()
        {
            StringBuilder sbSql = StringHelper.CreateBuilder();

            if (_projection == null)
            {
                sbSql.Append(_table.ToSqlSelect(_factory.Dialect, _factory.Mapping.Catalog, _factory.Mapping.Schema, false));
            }
            else
            {
                sbSql.Append("SELECT ")
                    .Append(_projection.Render(this))
                    .Append(" FROM ")
                    .Append(_table.GetQualifiedName(_factory.Dialect, _factory.Mapping.Catalog, _factory.Mapping.Schema));
            }

            GenerateFragment(sbSql, "WHERE", _conditions, " AND ");

            if (_projection != null && _projection.Grouped)
                sbSql.Append(" GROUP BY ").Append(_projection.ToGroupString(this));

            return sbSql.ToString();
        }

        private String GenerateOrder()
        {
            if (_orders.Count > 0)
            {
                StringBuilder sb = StringHelper.CreateBuilder()
                    .Append("ORDER BY ");
                StringHelper.AppendItemsWithSeperator(_orders, ",", delegate(Order order)
                {
                    sb.Append(order.Render(this));
                }, sb);
                return sb.ToString();
            }
            else
                return null;
        }

        private void GenerateFragment(StringBuilder sb, String prefix, IList<IExpression> exps, String sep)
        {
            if (exps.Count > 0)
            {
                sb.Append(" ").Append(prefix).Append(" ");
                StringHelper.AppendItemsWithSeperator(exps, sep, delegate(IExpression exp)
                {
                    sb.Append(exp.Render(this));
                }, sb);
            }
        }

        public String ToSqlString(BetweenExpression between)
        {
            return StringHelper.CreateBuilder()
                .Append(between.Expression.Render(this))
                .Append(" between ")
                .Append(between.Lower.Render(this))
                .Append(" and ")
                .Append(between.Upper.Render(this))
                .ToString();
        }

        public String ToSqlString(LikeExpression like)
        {
            StringBuilder sb = StringHelper.CreateBuilder();

            if (like.IgnoreCase)
                sb.Append(_factory.Dialect.LowercaseFunction)
                    .Append('(').Append(like.Expression.Render(this)).Append(')');
            else
                sb.Append(like.Expression.Render(this));

            sb.Append(" like ");

            String value = like.MatchMode.ToMatchString(like.Value);

            if (Parameterized)
                sb.Append(RegisterParam(value));
            else
                sb.Append("'").Append(value).Append("'");

            if (like.EscapeChar != null)
                sb.Append(" escape \'").Append(like.EscapeChar).Append("\'");

            return sb.ToString();
        }

        public String ToSqlString(IlikeExpression ilike)
        {
            StringBuilder sb = StringHelper.CreateBuilder();

            if (_factory.Dialect is PostgreSQLDialect)
                sb.Append(ilike.Expression.Render(this))
                    .Append(" ilike ");
            else
                sb.Append(_factory.Dialect.LowercaseFunction)
                    .Append('(').Append(ilike.Expression.Render(this)).Append(')')
                    .Append(" like ");

            String value = ilike.MatchMode.ToMatchString(ilike.Value);

            if (Parameterized)
                sb.Append(RegisterParam(value));
            else
                sb.Append("'").Append(value).Append("'");

            return sb.ToString();
        }

        public String ToSqlString(InExpression inexp)
        {
            StringBuilder sb = StringHelper.CreateBuilder()
                .Append(inexp.Expression.Render(this))
                .Append(" in (");

            StringHelper.AppendItemsWithComma(inexp.Values, delegate(IExpression exp)
            {
                sb.Append(exp.Render(this));
            }, sb);

            return sb.Append(")").ToString();
        }

        public String ToSqlString(Junction junction)
        {
            if (0 == junction.Expressions.Count)
                return "1=1";

            StringBuilder sb = StringHelper.CreateBuilder().Append("(");

            StringHelper.AppendItemsWithSeperator(junction.Expressions, ' ' + junction.Op + ' ',
                delegate(IExpression exp)
                {
                    sb.Append(exp.Render(this));
                }, sb);

            return sb.Append(')').ToString();
        }

        public String ToSqlString(LogicalExpression logical)
        {
            return StringHelper.CreateBuilder()
                .Append('(')
                .Append(logical.Left.Render(this))
                .Append(' ')
                .Append(logical.Op)
                .Append(' ')
                .Append(logical.Right.Render(this))
                .Append(')')
                .ToString();
        }

        public String ToSqlString(NotExpression not)
        {
            if (_factory.Dialect is MySQLDialect)
                return "not (" + not.Expression.Render(this) + ')';
            else
                return "not " + not.Expression.Render(this);
        }

        public String ToSqlString(NotNullExpression notNull)
        {
            return notNull.Expression.Render(this) + " is not null";
        }

        public String ToSqlString(NullExpression nullexp)
        {
            return nullexp.Expression.Render(this) + " is null";
        }

        public String ToSqlString(PlainExpression plain)
        {
            return plain.ToString();
        }

        public String ToSqlString(ValueExpression value)
        {
            if (Parameterized)
                return RegisterParam(value.Value);
            else
                return value.ToString();
        }

        public String ToSqlString(FieldExpression field)
        {
            Mapping.Column column = _table.FindColumnByFieldName(field.Filed);
            if (column == null)
                return field.ToString();
            else
                return column.GetQuotedName(_factory.Dialect);
        }

        public String ToSqlString(Order order)
        {
            return StringHelper.CreateBuilder()
                .Append(order.Expression.Render(this))
                .Append((order.Ascending ? " ASC" : " DESC"))
                .ToString();
        }

        [Obsolete]
        public String ToSqlString(From.Table table)
        {
            throw new NotImplementedException();
        }

        public String ToSqlString(Function function)
        {
            ISQLFunction func = _factory.Dialect.FindFunction(function.FunctionName);
            if (func == null)
                // TODO throw an exception
                throw new MappingException("Function not found");
            List<Object> list = new List<Object>();
            foreach (IExpression exp in function.Arguments)
            {
                list.Add(exp.Render(this));
            }
            return func.Render(list, _factory as IConnectionFactory);
        }

        public String ToSqlString(SimpleExpression simple)
        {
            return StringHelper.CreateBuilder()
                .Append('(')
                .Append(simple.Left.Render(this))
                .Append(' ')
                .Append(simple.Op)
                .Append(' ')
                .Append(simple.Right.Render(this))
                .Append(')')
                .ToString();
        }

        public String ToSqlString(PropertyExpression property)
        {
            return StringHelper.CreateBuilder()
                .Append('(')
                .Append(property.PropertyName.Render(this))
                .Append(' ')
                .Append(property.Op)
                .Append(' ')
                .Append(property.OtherPropertyName.Render(this))
                .Append(')')
                .ToString();
        }

        public String ToSqlString(AggregateProjection aggregateProjection)
        {
            ISQLFunction func = _factory.Dialect.FindFunction(aggregateProjection.FunctionName);
            if (func == null)
                // TODO throw an exception
                throw new MappingException("Function not found");
            return Alias(func.Render(aggregateProjection.BuildFunctionParameterList(this), _factory as IConnectionFactory), aggregateProjection.Alias);
        }

        public String ToSqlString(RowCountProjection projection)
        {
            ISQLFunction func = _factory.Dialect.FindFunction("count");
            if (func == null)
                throw new MappingException("count function not found");
            return Alias(func.Render(RowCountProjection.Arguments, _factory as IConnectionFactory), projection.Alias);
        }

        public String ToSqlString(PropertyProjection propertyProjection)
        {
            return Alias(Clauses.Field(propertyProjection.PropertyName).Render(this), propertyProjection.Alias);
        }

        public String ToSqlString(ExpressionProjection projection)
        {
            return Alias(projection.Expression.Render(this), projection.Alias);
        }

        private String Alias(String exp, String alias)
        { 
            return String.IsNullOrEmpty(alias) ? exp : (exp + " AS " + _factory.Dialect.Quote(alias));
        }
    }

    class Criteria<T> : Criteria, ICriteria<T>, ICriteriaRender
    {
        public Criteria(IConnection connection, IConnectionFactorySupport factory)
            : base(typeof(T), connection, factory)
        {
        }

        public new ICriteria<T> Add(IExpression condition)
        {
            base.Add(condition);
            return this;
        }

        public new ICriteria<T> AddOrder(Order order)
        {
            base.AddOrder(order);
            return this;
        }

        public new ICriteria<T> SetProjection(IProjection projection)
        {
            base.SetProjection(projection);
            return this;
        }

        public new IEnumerable<T> List()
        {
            return List(-1, 0);
        }

        public new IEnumerable<T> List(Int32 total = -1, Int32 offset = 0)
        {
            Total = total;
            Offset = offset;
            return Enumerable.ToList(_connection.Query<T>(ToSqlString(), Parameters));
        }

        public new T SingleOrDefault()
        {
            return Enumerable.SingleOrDefault(List(1));
        }
    }

    /// <summary>
    /// Renders criterion fragments.
    /// </summary>
    public interface ICriteriaRender
    {
        /// <summary>
        /// Renders <see cref="BetweenExpression"/>.
        /// </summary>
        String ToSqlString(BetweenExpression between);
        /// <summary>
        /// Renders <see cref="LikeExpression"/>.
        /// </summary>
        String ToSqlString(LikeExpression like);
        /// <summary>
        /// Renders <see cref="IlikeExpression"/>.
        /// </summary>
        String ToSqlString(IlikeExpression ilike);
        /// <summary>
        /// Renders <see cref="InExpression"/>.
        /// </summary>
        String ToSqlString(InExpression inexp);
        /// <summary>
        /// Renders <see cref="Junction"/>.
        /// </summary>
        String ToSqlString(Junction junction);
        /// <summary>
        /// Renders <see cref="LogicalExpression"/>.
        /// </summary>
        String ToSqlString(LogicalExpression logicalExpression);
        /// <summary>
        /// Renders <see cref="NotExpression"/>.
        /// </summary>
        String ToSqlString(NotExpression notExpression);
        /// <summary>
        /// Renders <see cref="NotNullExpression"/>.
        /// </summary>
        String ToSqlString(NotNullExpression notNullExpression);
        /// <summary>
        /// Renders <see cref="NullExpression"/>.
        /// </summary>
        String ToSqlString(NullExpression nullExpression);
        /// <summary>
        /// Renders <see cref="PlainExpression"/>.
        /// </summary>
        String ToSqlString(PlainExpression plainExpression);
        /// <summary>
        /// Renders <see cref="ValueExpression"/>.
        /// </summary>
        String ToSqlString(ValueExpression valueExpression);
        /// <summary>
        /// Renders <see cref="FieldExpression"/>.
        /// </summary>
        String ToSqlString(FieldExpression fieldExpression);
        /// <summary>
        /// Renders <see cref="Order"/>.
        /// </summary>
        String ToSqlString(Order order);
        /// <summary>
        /// Renders <see cref="From.Table"/>.
        /// </summary>
        [Obsolete]
        String ToSqlString(From.Table table);
        /// <summary>
        /// Renders <see cref="Function"/>.
        /// </summary>
        String ToSqlString(Function function);
        /// <summary>
        /// Renders <see cref="SimpleExpression"/>.
        /// </summary>
        String ToSqlString(SimpleExpression simpleExpression);
        /// <summary>
        /// Renders <see cref="PropertyExpression"/>.
        /// </summary>
        String ToSqlString(PropertyExpression propertyExpression);
        /// <summary>
        /// Renders <see cref="AggregateProjection"/>.
        /// </summary>
        String ToSqlString(AggregateProjection aggregateProjection);
        /// <summary>
        /// Renders <see cref="RowCountProjection"/>.
        /// </summary>
        String ToSqlString(RowCountProjection projection);
        /// <summary>
        /// Renders <see cref="PropertyProjection"/>.
        /// </summary>
        String ToSqlString(PropertyProjection projection);
        /// <summary>
        /// Renders <see cref="ExpressionProjection"/>.
        /// </summary>
        String ToSqlString(ExpressionProjection projection);
    }
}
