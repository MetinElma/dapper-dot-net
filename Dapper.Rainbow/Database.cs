/*
 License: http://www.apache.org/licenses/LICENSE-2.0 
 Home page: http://code.google.com/p/dapper-dot-net/
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using System.Data;
using Dapper;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Data.Common;
using System.Diagnostics;
using System.Reflection.Emit;

namespace Dapper
{
    /// <summary>
    /// A container for a database, assumes all the tables have an Id column named Id
    /// </summary>
    /// <typeparam name="TDatabase"></typeparam>
    public abstract class Database<TDatabase> : IDisposable where TDatabase : Database<TDatabase>, new()
    {
        public class Table<T, TId>
        {
            internal Database<TDatabase> database;
            internal string tableName;
            internal string likelyTableName;

            public Table(Database<TDatabase> database, string likelyTableName)
            {
                this.database = database;
                this.likelyTableName = likelyTableName;
            }

            public string TableName
            {
                get
                {
                    tableName = tableName ?? database.DetermineTableName<T>(likelyTableName);
                    return tableName;
                }
            }

            /// <summary>
            /// Insert a row into the db
            /// </summary>
            /// <param name="data">Either DynamicParameters or an anonymous type or concrete type. "Id" is assumed as the name of the primary key column.</param>
            /// <returns></returns>
            public virtual int? Insert(dynamic data)
            {
                return Insert("Id", data);
            }

            /// <summary>
            /// Insert a row into the db. Use this method if the name of the primary key column is not "Id".
            /// </summary>
            /// <param name="data">Either DynamicParameters or an anonymous type or concrete type</param>
            /// <param name="idName">Name of the Primary Key Column</param>
            /// <returns></returns>
            public virtual int? Insert(string idName, dynamic data)
            {
                var o = (object)data;
                List<string> paramNames = GetParamNames(o);
                paramNames.Remove(idName);
                //Nothing to insert if there are no parameters. (This is the case when the given data is empty.)
                if (paramNames.Count() == 0)
                {
                    return null;
                }
                string cols = string.Join(",", paramNames);
                string cols_params = string.Join(",", paramNames.Select(p => "@" + p));
                var sql = "set nocount on insert " + TableName + " (" + cols + ") values (" + cols_params + ") select cast(scope_identity() as int)";

                return database.Query<int?>(sql, o).Single();
            }

            /// <summary>
            /// Update a record in the DB. "Id" is assumed as the name of the primary key column.
            /// </summary>
            /// <param name="id"></param>
            /// <param name="data"></param>
            /// <returns></returns>
            public int Update(TId id, dynamic data)
            {
                return Update("Id", id, data);
            }

            /// <summary>
            /// Update a record in the DB. Use this method if the name of the primary key column is not "Id".
            /// </summary>
            /// <param name="idName">Name of the Primary Key Column</param>
            /// <param name="id"></param>
            /// <param name="data"></param>
            /// <returns></returns>
            public int Update(string idName, TId id, dynamic data)
            {
                List<string> paramNames = GetParamNames((object)data);
                var paramNamesWithoutPrimaryKey = paramNames.Where(n => n != idName).Select(p => p + "= @" + p);
                if (paramNamesWithoutPrimaryKey.Count() == 0)
                {
                    return 0;
                }
                var builder = new StringBuilder();
                builder.Append("update ").Append(TableName).Append(" set ");
                builder.AppendLine(string.Join(",", paramNamesWithoutPrimaryKey));
                builder.Append("where " + idName + " = @" + idName);

                DynamicParameters parameters = new DynamicParameters(data);
                parameters.Add(idName, id);
                return database.Execute(builder.ToString(), parameters);
            }

            /// <summary>
            /// Delete a record for the DB. "Id" is assumed as the name of the primary key column.
            /// </summary>
            /// <param name="id"></param>
            /// <returns></returns>
            public bool Delete(TId id)
            {
                return Delete("Id", id);
            }

            /// <summary>
            /// Delete a record for the DB. Use this method if the name of the primary key column is not "Id".
            /// </summary>
            /// <param name="idName">Name of the Primary Key Column</param>
            /// <param name="id"></param>
            /// <returns></returns>
            public bool Delete(string idName, TId id)
            {
                return database.Execute("delete from " + TableName + " where " + idName + " = @tid", new { tid = id }) > 0;
            }

            /// <summary>
            /// Grab a record with a particular Id from the DB . "Id" is assumed as the name of the primary key column.
            /// </summary>
            /// <param name="id"></param>
            /// <returns></returns>
            public T Get(TId id)
            {
                return Get("Id", id);
            }

            /// <summary>
            /// Grab a record with a particular Id from the DB. Use this method if the name of the primary key column is not "Id".
            /// </summary>
            /// <param name="idName">Name of the Primary Key Column</param>
            /// <param name="id"></param>
            /// <returns></returns>
            public T Get(string idName, TId id)
            {
                return database.Query<T>("select * from " + TableName + " where " + idName + " = @tid", new { tid = id }).FirstOrDefault();
            }

            public virtual T First()
            {
                return database.Query<T>("select top 1 * from " + TableName).FirstOrDefault();
            }

            public IEnumerable<T> All()
            {
                return database.Query<T>("select * from " + TableName);
            }

            static ConcurrentDictionary<Type, List<string>> paramNameCache = new ConcurrentDictionary<Type, List<string>>();

            internal static List<string> GetParamNames(object o)
            {
                if (o is DynamicParameters)
                {
                    return (o as DynamicParameters).ParameterNames.ToList();
                }

                List<string> paramNames;
                if (!paramNameCache.TryGetValue(o.GetType(), out paramNames))
                {
                    paramNames = new List<string>();
                    foreach (var prop in o.GetType().GetProperties(BindingFlags.GetProperty | BindingFlags.Instance | BindingFlags.Public))
                    {
                        paramNames.Add(prop.Name);
                    }
                    paramNameCache[o.GetType()] = paramNames;
                }
                return paramNames;
            }
        }

        public class Table<T> : Table<T, int>
        {
            public Table(Database<TDatabase> database, string likelyTableName)
                : base(database, likelyTableName)
            {
            }
        }

        DbConnection connection;
        int commandTimeout;
        DbTransaction transaction;


        public static TDatabase Init(DbConnection connection, int commandTimeout)
        {
            TDatabase db = new TDatabase();
            db.InitDatabase(connection, commandTimeout);
            return db;
        }

        internal static Action<TDatabase> tableConstructor;

        internal void InitDatabase(DbConnection connection, int commandTimeout)
        {
            this.connection = connection;
            this.commandTimeout = commandTimeout;
            if (tableConstructor == null)
            {
                tableConstructor = CreateTableConstructorForTable();
            }

            tableConstructor(this as TDatabase);
        }

        internal virtual Action<TDatabase> CreateTableConstructorForTable()
        {
            return CreateTableConstructor(typeof(Table<>));
        }

        public void BeginTransaction(IsolationLevel isolation = IsolationLevel.ReadCommitted)
        {
            transaction = connection.BeginTransaction(isolation);
        }

        public void CommitTransaction()
        {
            transaction.Commit();
            transaction = null;
        }

        public void RollbackTransaction()
        {
            transaction.Rollback();
            transaction = null;
        }

        protected Action<TDatabase> CreateTableConstructor(Type tableType)
        {
            var dm = new DynamicMethod("ConstructInstances", null, new Type[] { typeof(TDatabase) }, true);
            var il = dm.GetILGenerator();

            var setters = GetType().GetProperties()
                .Where(p => p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == tableType)
                .Select(p => Tuple.Create(
                        p.GetSetMethod(true),
                        p.PropertyType.GetConstructor(new Type[] { typeof(TDatabase), typeof(string) }),
                        p.Name,
                        p.DeclaringType
                 ));

            foreach (var setter in setters)
            {
                il.Emit(OpCodes.Ldarg_0);
                // [db]

                il.Emit(OpCodes.Ldstr, setter.Item3);
                // [db, likelyname]

                il.Emit(OpCodes.Newobj, setter.Item2);
                // [table]

                var table = il.DeclareLocal(setter.Item2.DeclaringType);
                il.Emit(OpCodes.Stloc, table);
                // []

                il.Emit(OpCodes.Ldarg_0);
                // [db]

                il.Emit(OpCodes.Castclass, setter.Item4);
                // [db cast to container]

                il.Emit(OpCodes.Ldloc, table);
                // [db cast to container, table]

                il.Emit(OpCodes.Callvirt, setter.Item1);
                // []
            }

            il.Emit(OpCodes.Ret);
            return (Action<TDatabase>)dm.CreateDelegate(typeof(Action<TDatabase>));
        }

        static ConcurrentDictionary<Type, string> tableNameMap = new ConcurrentDictionary<Type, string>();
        private string DetermineTableName<T>(string likelyTableName)
        {
            string name;

            if (!tableNameMap.TryGetValue(typeof(T), out name))
            {
                name = likelyTableName;
                if (!TableExists(name))
                {
                    name = "[" + typeof(T).Name + "]";
                }

                tableNameMap[typeof(T)] = name;
            }
            return name;
        }

        private bool TableExists(string name)
        {
            string schemaName = null;

            name = name.Replace("[", "");
            name = name.Replace("]", "");

            if (name.Contains("."))
            {
                var parts = name.Split('.');
                if (parts.Count() == 2)
                {
                    schemaName = parts[0];
                    name = parts[1];
                }
            }

            var builder = new StringBuilder("select 1 from INFORMATION_SCHEMA.TABLES where ");
            if (!String.IsNullOrEmpty(schemaName)) builder.Append("TABLE_SCHEMA = @schemaName AND ");
            builder.Append("TABLE_NAME = @name");

            return connection.Query(builder.ToString(), new { schemaName, name }, transaction: transaction).Count() == 1;
        }

        public int Execute(string sql, dynamic param = null)
        {
            return SqlMapper.Execute(connection, sql, param as object, transaction, commandTimeout: this.commandTimeout);
        }

        public IEnumerable<T> Query<T>(string sql, dynamic param = null, bool buffered = true)
        {
            return SqlMapper.Query<T>(connection, sql, param as object, transaction, buffered, commandTimeout);
        }

        public IEnumerable<TReturn> Query<TFirst, TSecond, TReturn>(string sql, Func<TFirst, TSecond, TReturn> map, dynamic param = null, IDbTransaction transaction = null, bool buffered = true, string splitOn = "Id", int? commandTimeout = null)
        {
            return SqlMapper.Query(connection, sql, map, param as object, transaction, buffered, splitOn);
        }

        public IEnumerable<TReturn> Query<TFirst, TSecond, TThird, TReturn>(string sql, Func<TFirst, TSecond, TThird, TReturn> map, dynamic param = null, IDbTransaction transaction = null, bool buffered = true, string splitOn = "Id", int? commandTimeout = null)
        {
            return SqlMapper.Query(connection, sql, map, param as object, transaction, buffered, splitOn);
        }

        public IEnumerable<TReturn> Query<TFirst, TSecond, TThird, TFourth, TReturn>(string sql, Func<TFirst, TSecond, TThird, TFourth, TReturn> map, dynamic param = null, IDbTransaction transaction = null, bool buffered = true, string splitOn = "Id", int? commandTimeout = null)
        {
            return SqlMapper.Query(connection, sql, map, param as object, transaction, buffered, splitOn);
        }

        public IEnumerable<TReturn> Query<TFirst, TSecond, TThird, TFourth, TFifth, TReturn>(string sql, Func<TFirst, TSecond, TThird, TFourth, TFifth, TReturn> map, dynamic param = null, IDbTransaction transaction = null, bool buffered = true, string splitOn = "Id", int? commandTimeout = null)
        {
            return SqlMapper.Query(connection, sql, map, param as object, transaction, buffered, splitOn);
        }

        public IEnumerable<dynamic> Query(string sql, dynamic param = null, bool buffered = true)
        {
            return SqlMapper.Query(connection, sql, param as object, transaction, buffered);
        }

        public Dapper.SqlMapper.GridReader QueryMultiple(string sql, dynamic param = null, IDbTransaction transaction = null, int? commandTimeout = null, CommandType? commandType = null)
        {
            return SqlMapper.QueryMultiple(connection, sql, param, transaction, commandTimeout, commandType);
        }


        public void Dispose()
        {
            if (connection.State != ConnectionState.Closed)
            {
                if (transaction != null)
                {
                    transaction.Rollback();
                }

                connection.Close();
                connection = null;
            }
        }
    }
}