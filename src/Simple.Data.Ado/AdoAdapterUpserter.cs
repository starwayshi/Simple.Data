﻿namespace Simple.Data.Ado
{
    using System;
    using System.Collections.Generic;
    using System.Data;
    using System.Linq;
    using System.Threading.Tasks;
    using Extensions;

    class AdoAdapterUpserter
    {
        private readonly AdoAdapter _adapter;
        private readonly IDbConnection _connection;
        private readonly IDbTransaction _transaction;

        public AdoAdapterUpserter(AdoAdapter adapter) : this(adapter, (IDbTransaction)null)
        {
        }

        public AdoAdapterUpserter(AdoAdapter adapter, IDbConnection connection)
        {
            _adapter = adapter;
            _connection = connection;
        }

        public AdoAdapterUpserter(AdoAdapter adapter, IDbTransaction transaction)
        {
            _adapter = adapter;
            _transaction = transaction;
            if (transaction != null) _connection = transaction.Connection;
        }

        public async Task<IDictionary<string, object>> Upsert(string tableName, IReadOnlyDictionary<string, object> data, SimpleExpression criteria, bool resultRequired)
        {
            var connection = _connection ?? _adapter.CreateConnection();
            using (connection.MaybeDisposable())
            {
                await _adapter.CommandExecutor.OpenIfClosed(connection);
                return await Upsert(tableName, data, criteria, resultRequired, connection);
            }
        }

        private async Task<IDictionary<string, object>> Upsert(string tableName, IReadOnlyDictionary<string, object> data, SimpleExpression criteria, bool resultRequired,
                                   IDbConnection connection)
        {
            var finder = _transaction == null
                             ? new AdoAdapterFinder(_adapter, connection)
                             : new AdoAdapterFinder(_adapter, _transaction);

            var existing = await finder.FindOne(tableName, criteria);
            if (existing != null)
            {
                // Don't update columns used as criteria
                var keys = criteria.GetOperandsOfType<ObjectReference>().Select(o => o.GetName().Homogenize());
                var updateData = data.Where(kvp => keys.All(k => k != kvp.Key.Homogenize())).ToReadOnlyDictionary();
                if (updateData.Count == 0)
                {
                    return existing;
                }

                var commandBuilder = new UpdateHelper(_adapter.GetSchema()).GetUpdateCommand(tableName, updateData, criteria);
                if (_transaction == null)
                {
                    _adapter.Execute(commandBuilder, connection);
                }
                else
                {
                    _adapter.Execute(commandBuilder, _transaction);
                }
                return resultRequired ? await finder.FindOne(tableName, criteria) : null;
            }
            var inserter = _transaction == null
                               ? new AdoAdapterInserter(_adapter, connection)
                               : new AdoAdapterInserter(_adapter, _transaction);
            return await inserter.Insert(tableName, data, resultRequired);
        }


        public async Task<IEnumerable<IDictionary<string, object>>> UpsertMany(string tableName, IList<IReadOnlyDictionary<string, object>> list, bool isResultRequired, Func<IReadOnlyDictionary<string, object>, Exception, bool> errorCallback)
        {
            var results = new List<IDictionary<string, object>>();
            foreach (var row in list)
            {
                IDictionary<string, object> result;
                try
                {
                    var key = _adapter.GetKey(tableName, row);
                    if (key.Count == 0)
                    {
                        result = await new AdoAdapterInserter(_adapter).Insert(tableName, row, isResultRequired);
                    }
                    else
                    {
                        var criteria = ExpressionHelper.CriteriaDictionaryToExpression(tableName,
                                                                                       key);
                        result = await Upsert(tableName, row, criteria, isResultRequired);
                    }
                }
                catch (Exception ex)
                {
                    if (errorCallback(row, ex)) continue;
                    throw;
                }

                results.Add(result);
            }
            return results;
        }
        
        public async Task<IEnumerable<IDictionary<string, object>>> UpsertMany(string tableName, IList<IReadOnlyDictionary<string, object>> list, IList<string> keyFieldNames, bool isResultRequired, Func<IReadOnlyDictionary<string, object>, Exception, bool> errorCallback)
        {
            var results = new List<IDictionary<string, object>>();
            foreach (var row in list)
            {
                IDictionary<string, object> result;
                try
                {
                    var copy = row;
                    var criteria = GetCriteria(tableName, keyFieldNames, ref copy);
                    result = await Upsert(tableName, copy, criteria, isResultRequired);
                }
                catch (Exception ex)
                {
                    if (errorCallback(row, ex)) continue;
                    throw;
                }

                results.Add(result);
            }
            return results;
        }

        private static SimpleExpression GetCriteria(string tableName, IEnumerable<string> criteriaFieldNames,
                                                      ref IReadOnlyDictionary<string, object> record)
        {
            var criteria = new Dictionary<string, object>();
            var writeable = record.ToDictionary();

            foreach (var criteriaFieldName in criteriaFieldNames)
            {
                var name = criteriaFieldName;
                var keyValuePair = record.SingleOrDefault(kvp => kvp.Key.Homogenize().Equals(name.Homogenize()));
                if (string.IsNullOrWhiteSpace(keyValuePair.Key))
                {
                    throw new InvalidOperationException("Key field value not set.");
                }

                criteria.Add(criteriaFieldName, keyValuePair.Value);
                writeable.Remove(keyValuePair);
            }
            record = writeable.ToReadOnly();
            return ExpressionHelper.CriteriaDictionaryToExpression(tableName, criteria);
        }
    }
}