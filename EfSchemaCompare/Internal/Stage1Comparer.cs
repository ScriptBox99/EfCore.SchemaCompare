﻿// Copyright (c) 2020 Jon P Smith, GitHub: JonPSmith, web: http://www.thereformedprogrammer.net/
// Licensed under MIT license. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Scaffolding.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

[assembly: InternalsVisibleTo("Test")]

namespace EfSchemaCompare.Internal
{
    internal class Stage1Comparer
    {
        private const string NoPrimaryKey = "- no primary key -";
        private const string ViewPrimaryKey = "- view primary key -";

        private readonly IModel _model;
        private readonly string _dbContextName;
        private readonly IRelationalTypeMappingSource _relationalTypeMapping;
        private readonly IReadOnlyList<CompareLog> _ignoreList;
        private readonly StringComparer _caseComparer;
        private readonly StringComparison _caseComparison;

        private string _defaultSchema;
        private Dictionary<string, DatabaseTable> _tableViewDict;
        private bool _hasErrors;

        private readonly List<CompareLog> _logs;
        public IReadOnlyList<CompareLog> Logs => _logs.ToImmutableList();

        public Stage1Comparer(DbContext context, CompareEfSqlConfig config = null, List<CompareLog> logs = null)
        {
            _model = context.Model;
            _dbContextName = context.GetType().Name;
            _relationalTypeMapping = context.GetService<IRelationalTypeMappingSource>();
            _logs = logs ?? new List<CompareLog>();
            _ignoreList = config?.LogsToIgnore ?? new List<CompareLog>();
            _caseComparer = StringComparer.CurrentCulture;          //Turned off CaseComparer as doesn't work with EF Core 5
            _caseComparison = _caseComparer.GetStringComparison();
        }


        public bool CompareModelToDatabase(DatabaseModel databaseModel)
        {
            _defaultSchema = databaseModel.DefaultSchema;
            var dbLogger = new CompareLogger2(CompareType.DbContext, _dbContextName, _logs, _ignoreList, () => _hasErrors = true);

            //Check things about the database, such as sequences
            dbLogger.MarkAsOk(_dbContextName);
            CheckDatabaseOk(_logs.Last(), _model, databaseModel);

            _tableViewDict = databaseModel.Tables.ToDictionary(x => x.FormSchemaTableFromDatabase(_defaultSchema), _caseComparer);
            var entitiesNotMappedToTableOrView = _model.GetEntityTypes().Where(x => x.FormSchemaTableFromModel() == null).ToList();
            if (entitiesNotMappedToTableOrView.Any())
                dbLogger.MarkAsNotChecked(null,
                    string.Join(", ", entitiesNotMappedToTableOrView.Select(x => x.ClrType.Name)), CompareAttributes.NotMappedToDatabase);
            foreach (var entityType in _model.GetEntityTypes().Where(x => !entitiesNotMappedToTableOrView.Contains(x)))
            {
                var logger = new CompareLogger2(CompareType.Entity, entityType.ClrType.Name, _logs.Last().SubLogs, _ignoreList, () => _hasErrors = true);
                if (_tableViewDict.ContainsKey(entityType.FormSchemaTableFromModel()))
                {
                    var databaseTable = _tableViewDict[entityType.FormSchemaTableFromModel()];
                    //Checks for table matching
                    var log = logger.MarkAsOk(entityType.FormSchemaTableFromModel());
                    if(entityType.GetTableName() != null)
                    {
                        var xx = entityType.FindPrimaryKey().GetReferencingForeignKeys().ToList();
                        var yy = entityType.FindPrimaryKey();
                        //Its not a view
                        logger.CheckDifferent(entityType.FindPrimaryKey()?.GetName() ?? NoPrimaryKey,
                            databaseTable.PrimaryKey?.Name ?? NoPrimaryKey,
                            CompareAttributes.ConstraintName, _caseComparison);
                    }
                    CompareColumns(log, entityType, databaseTable);
                    CompareForeignKeys(log, entityType, databaseTable);
                    CompareIndexes(log, entityType, databaseTable);
                }
                else
                {
                    logger.NotInDatabase(entityType.FormSchemaTableFromModel(), CompareAttributes.TableName);
                }
            }
            return _hasErrors;
        }

        private void CheckDatabaseOk(CompareLog log, IModel modelRel, DatabaseModel databaseModel)
        {
            //Check sequences
            //var logger = new CompareLogger2(CompareType.Sequence, <sequence name>, _logs);
        }

        private void CompareForeignKeys(CompareLog log, IEntityType entityType, DatabaseTable table)
        {
            var fKeyDict = table.ForeignKeys.ToDictionary(x => x.Name, _caseComparer);

            foreach (var entityFKey in entityType.GetForeignKeys())
            {
                var entityFKeyprops = entityFKey.Properties;
                var constraintName = entityFKey.GetConstraintName();
                var logger = new CompareLogger2(CompareType.ForeignKey, constraintName, log.SubLogs, _ignoreList, () => _hasErrors = true);
                if (IgnoreForeignKeyIfInSameTableOrTpT(entityType, entityFKey, table))
                    continue;
                if (fKeyDict.ContainsKey(constraintName))
                {
                    //Now check every foreign key
                    var error = false;
                    var thisKeyCols = fKeyDict[constraintName].Columns.ToDictionary(x => x.Name, _caseComparer);
                    foreach (var fKeyProp in entityFKeyprops)
                    {
                        var columnName = GetColumnNameTakingIntoAccountSchema( fKeyProp, table);
                        if (!thisKeyCols.ContainsKey(columnName))
                        {
                            logger.NotInDatabase(columnName);
                            error = true;
                        }
                    }
                    error |= logger.CheckDifferent(entityFKey.DeleteBehavior.ToString(),
                        fKeyDict[constraintName].OnDelete.ConvertReferentialActionToDeleteBehavior(entityFKey.DeleteBehavior),
                            CompareAttributes.DeleteBehavior, _caseComparison);
                    if (!error)
                        logger.MarkAsOk(constraintName);
                }
                else
                {
                    logger.NotInDatabase(constraintName, CompareAttributes.ConstraintName);
                }
            }
        }

        
        private bool IgnoreForeignKeyIfInSameTableOrTpT(IEntityType entityType, IForeignKey entityFKey, DatabaseTable table)
        {
            //see https://github.com/aspnet/EntityFrameworkCore/issues/10345#issuecomment-345841191
            var fksPropsInOneTable = entityFKey.Properties.All(x =>
                string.Equals(x.DeclaringEntityType.FormSchemaTableFromModel(), table.FormSchemaTableFromDatabase(_defaultSchema), _caseComparison));
            var fksPropsColumnNames = entityFKey.Properties.Select(p => GetColumnNameTakingIntoAccountSchema(p, table));
            var pkPropsColumnNames =
                entityFKey.PrincipalKey.Properties.Select(p => GetColumnNameTakingIntoAccountSchema(p, 
                    _tableViewDict[p.DeclaringEntityType.FormSchemaTableFromModel()]));
            
            return fksPropsInOneTable && fksPropsColumnNames.SequenceEqual(pkPropsColumnNames);
        }

        private bool IsTpT(IEntityType entityType)
        {
            return entityType.BaseType != null && entityType.FormSchemaTableFromModel() != entityType.BaseType.FormSchemaTableFromModel();
        }

        private void CompareIndexes(CompareLog log, IEntityType entityType, DatabaseTable table)
        {
            var indexDict = DatabaseIndexData.GetIndexesAndUniqueConstraints(table).ToDictionary(x => x.Name, _caseComparer);
            foreach (var entityIdx in entityType.GetIndexes())
            {
                var entityIdxprops = entityIdx.Properties;
                var allColumnNames = string.Join(",", entityIdxprops
                    .Select(x => GetColumnNameTakingIntoAccountSchema(x, table)));
                var logger = new CompareLogger2(CompareType.Index, allColumnNames, log.SubLogs, _ignoreList, () => _hasErrors = true);
                var constraintName = entityIdx.GetDatabaseName();
                if (indexDict.ContainsKey(constraintName))
                {
                    //Now check every column in an index
                    var error = false;
                    var thisIdxCols = indexDict[constraintName].Columns.ToDictionary(x => x.Name, _caseComparer);
                    foreach (var idxProp in entityIdxprops)
                    {
                        var columnName = GetColumnNameTakingIntoAccountSchema(idxProp, table);
                        if (!thisIdxCols.ContainsKey(columnName))
                        {
                            logger.NotInDatabase(columnName);
                            error = true;
                        }
                    }
                    error |= logger.CheckDifferent(entityIdx.IsUnique.ToString(),
                        indexDict[constraintName].IsUnique.ToString(), CompareAttributes.Unique, _caseComparison);
                    if (!error)
                        logger.MarkAsOk(constraintName);
                }
                else
                {
                    logger.NotInDatabase(constraintName, CompareAttributes.IndexConstraintName);
                }
            }
        }

        /// <summary>
        /// This looks for
        /// 1. TPH and TPT
        /// 2. Nested Owned Types (can be required)
        /// 3. Table splitting where the class is optional
        /// If it finds one of these it returns a list of property names that aren't forces to be nullable (all other properties are forced to nullable types)
        /// Otherwise it returns null
        /// </summary>
        /// <param name="entityType"></param>
        /// <param name="table"></param>
        /// <returns>a list of property names that AREN'T forced to nullable</returns>
        private List<string> ClassIsAddedToTable(IEntityType entityType, DatabaseTable table)
        {
            //Check for THP or TPT
            if (entityType.BaseType != null)
            {
                //this is true if the BaseType is in the same table of the entity
                var isTpH = entityType.FormSchemaTableFromModel() == entityType.BaseType.FormSchemaTableFromModel();

                //We need to return the BaseType properties as not forces to nullable
                var propertiesNotForcedNull = entityType.BaseType.GetProperties()
                    .Select(x => GetColumnNameTakingIntoAccountSchema(x, table))
                    .ToList();
                if (!isTpH)
                    propertiesNotForcedNull.AddRange(entityType.GetProperties()
                        .Select(x => GetColumnNameTakingIntoAccountSchema(x, table)));
                return propertiesNotForcedNull;
            }

            //Now check for Nested Owned Types and table splitting 
            foreach (var foreignKey in entityType.GetForeignKeys())
            {
                var fkColumnNames = foreignKey
                    .Properties.Select(x => GetColumnNameTakingIntoAccountSchema(x, table)).ToList();
                var pkPropsColumnNames =
                    foreignKey.PrincipalKey
                        .Properties.Select(x => GetColumnNameTakingIntoAccountSchema(x, table))
                        .ToList();
                if (pkPropsColumnNames.SequenceEqual(fkColumnNames))
                {
                    //We are in a Nested Owned Types and table splitting

                    //This will find if the nested Owned Type is Require, in which case no properties are forced to nullable
                    var thisTableRelational = _model.GetRelationalModel().Tables.Single(x =>
                        x.FormSchemaTableFromITable(_defaultSchema) == table.FormSchemaTableFromDatabase(_defaultSchema));
                    var isRequired = thisTableRelational.PrimaryKey.MappedKeys.FirstOrDefault()?
                        .GetReferencingForeignKeys().FirstOrDefault()?.IsRequiredDependent ?? false;
                    if(isRequired)
                        //Owned Type is marked as required, so add all the properties as not forces to nullable
                        pkPropsColumnNames.AddRange(entityType.GetProperties()
                            .Select(x => GetColumnNameTakingIntoAccountSchema(x, table)));


                    return pkPropsColumnNames;
                }
            }

            return null;
        }

        private void CompareColumns(CompareLog log, IEntityType entityType, DatabaseTable table)
        {
            var isView = entityType.GetTableName() == null;
            var primaryKeyDict = table.PrimaryKey?.Columns.ToDictionary(x => x.Name, _caseComparer)
                                 ?? new Dictionary<string, DatabaseColumn>();
            var efPKeyConstraintName = isView ? NoPrimaryKey :  entityType.FindPrimaryKey()?.GetName() ?? NoPrimaryKey;
            bool pKeyError = false;
            var pKeyLogger = new CompareLogger2(CompareType.PrimaryKey, efPKeyConstraintName, log.SubLogs, _ignoreList,
                () =>
                {
                    pKeyError = true;  //extra set of pKeyError
                    _hasErrors = true;
                });
            if (!isView)
                pKeyLogger.CheckDifferent(efPKeyConstraintName, table.PrimaryKey?.Name ?? NoPrimaryKey,
                    CompareAttributes.ConstraintName, _caseComparison);

            var columnDict = table.Columns.ToDictionary(x => x.Name, _caseComparer);
            
            //This finds all the Owned Types and THP
            var pksOfClassAddedToTable = ClassIsAddedToTable(entityType, table);
            foreach (var property in entityType.GetProperties())
            {
                var colLogger = new CompareLogger2(CompareType.Property, property.Name, log.SubLogs, _ignoreList, () => _hasErrors = true);
                var columnName = GetColumnNameTakingIntoAccountSchema(property, table, isView);
                if (columnName == null)
                {
                    //This catches properties in TPH, split tables, and Owned Types where the properties are not mapped to the current table
                    continue;
                }
                if (columnDict.ContainsKey(columnName))
                {
                    var isNullable = pksOfClassAddedToTable?.Contains(columnName) == false;
                    var error = ComparePropertyToColumn(entityType, colLogger, property, columnDict[columnName], isNullable, isView);
                    //check for primary key
                    if (property.IsPrimaryKey() &&
                        //This remove TPH, Owned Types primary key checks
                        !isView != primaryKeyDict.ContainsKey(columnName))
                    {
                        if (!primaryKeyDict.ContainsKey(columnName))
                        {
                            pKeyLogger.NotInDatabase(columnName, CompareAttributes.ColumnName);
                            error = true;
                        }
                        else
                        {
                            pKeyLogger.ExtraInDatabase(columnName, CompareAttributes.ColumnName,
                                table.PrimaryKey.Name);
                        }
                    }

                    if (!error)
                    {
                        //There were no errors noted, so we mark it as OK
                        colLogger.MarkAsOk(columnName);
                    }
                }
                else
                {
                    colLogger.NotInDatabase(GetColumnNameTakingIntoAccountSchema(property, table), CompareAttributes.ColumnName);
                }
            }
            if (!pKeyError)
                pKeyLogger.MarkAsOk(efPKeyConstraintName);
        }

        private bool ComparePropertyToColumn(IEntityType entityType, CompareLogger2 logger, IProperty property, DatabaseColumn column, bool isNullable, bool isView)
        {
            var error = logger.CheckDifferent(property.GetColumnType(), column.StoreType, CompareAttributes.ColumnType, _caseComparison);
            error |= logger.CheckDifferent((property.IsNullable || isNullable).NullableAsString(), 
                column.IsNullable.NullableAsString(), CompareAttributes.Nullability, _caseComparison);
            error |= logger.CheckDifferent(property.GetComputedColumnSql().RemoveUnnecessaryBrackets(),
                column.ComputedColumnSql.RemoveUnnecessaryBrackets(), CompareAttributes.ComputedColumnSql, _caseComparison);
            if (property.GetComputedColumnSql() != null)
                error |= logger.CheckDifferent(property.GetIsStored()?.ToString() ?? false.ToString()
                    , column.IsStored.ToString(),
                    CompareAttributes.PersistentComputedColumn, _caseComparison);
            var defaultValue = property.GetDefaultValue() != null
                ? _relationalTypeMapping.FindMapping(property.GetDefaultValue().GetType())
                    .GenerateSqlLiteral(property.GetDefaultValue())
                : property.GetDefaultValueSql().RemoveUnnecessaryBrackets();
            error |= logger.CheckDifferent(defaultValue,
                    column.DefaultValueSql.RemoveUnnecessaryBrackets(), CompareAttributes.DefaultValueSql, _caseComparison);
            if (!isView && !IsTpT(entityType))
                error |= CheckValueGenerated(logger, property, column);
            return error;
        }

        //thanks to https://stackoverflow.com/questions/1749966/c-sharp-how-to-determine-whether-a-type-is-a-number
        private static HashSet<Type> IntegerTypes = new HashSet<Type>
        {
            typeof(byte), typeof(sbyte), typeof(short), typeof(ushort), typeof(int), typeof(uint), typeof(long), typeof(ulong)
        };

        private bool CheckValueGenerated(CompareLogger2 logger, IProperty property, DatabaseColumn column)
        {
            var colValGen = column.ValueGenerated.ConvertNullableValueGenerated(column.ComputedColumnSql, column.DefaultValueSql);
            if (colValGen == ValueGenerated.Never.ToString()
                //There is a case where the property is part of the primary key and the key is not set in the database
                && property.ValueGenerated == ValueGenerated.OnAdd
                && property.IsKey()
                //We assume that a integer of some form should be provided by the database
                && !IntegerTypes.Contains(property.ClrType))
                return false;
            return logger.CheckDifferent(property.ValueGenerated.ToString(),
                colValGen, CompareAttributes.ValueGenerated, _caseComparison);
        }

        private string GetColumnNameTakingIntoAccountSchema(IProperty property, DatabaseTable table,
            bool isView = false)
        {
            var modelSchema = table.Schema == _defaultSchema ? null : table.Schema;
            var columnName = isView
                ? property.GetColumnName(StoreObjectIdentifier.View(table.Name, modelSchema))
                : property.GetColumnName(StoreObjectIdentifier.Table(table.Name, modelSchema));
            return columnName;
        }

    }
}