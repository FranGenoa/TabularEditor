﻿extern alias json;

using Microsoft.AnalysisServices;
using json.Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TOM = Microsoft.AnalysisServices.Tabular;
using System.Threading;

namespace TabularEditor.TOMWrapper.Utils
{
    public class TabularDeployer
    {
        public static string GetTMSL(TOM.Database db, TOM.Server server, string targetDatabaseID, DeploymentOptions options, bool includeRestricted = false)
        {
            if (db == null) throw new ArgumentNullException("db");
            if (string.IsNullOrWhiteSpace(targetDatabaseID)) throw new ArgumentNullException("targetDatabaseID");
            if (options.DeployRoleMembers && !options.DeployRoles) throw new ArgumentException("Cannot deploy Role Members when Role deployment is disabled.");

            if (server.Databases.Contains(targetDatabaseID) && options.DeployMode == DeploymentMode.CreateDatabase) throw new ArgumentException("The specified database already exists.");

            if (!server.Databases.Contains(targetDatabaseID)) return DeployNewTMSL(db, targetDatabaseID, options, includeRestricted);
            else return DeployExistingTMSL(db, server, targetDatabaseID, options, includeRestricted);

            // TODO: Check if invalid CalculatedTableColumn perspectives/translations can give us any issues here
            // Should likely be handled similar to what we do in TabularModelHandler.SaveDB()
        }

        public static DeploymentResult Deploy(TabularModelHandler handler, string targetConnectionString, string targetDatabaseName)
        {
            return Deploy(handler.Database, targetConnectionString, targetDatabaseName, DeploymentOptions.Default, CancellationToken.None);
        }
        public static DeploymentResult Deploy(TabularModelHandler handler, string targetConnectionString, string targetDatabaseName, DeploymentOptions options, CancellationToken cancellationToken = default(CancellationToken))
        {
            return Deploy(handler.Database, targetConnectionString, targetDatabaseName, options, cancellationToken);
        }

        internal static DeploymentResult Deploy(TOM.Database db, string targetConnectionString, string targetDatabaseName)
        {
            return Deploy(db, targetConnectionString, targetDatabaseName, DeploymentOptions.Default, CancellationToken.None);
        }

        public static void SaveModelMetadataBackup(string connectionString, string targetDatabaseID, string backupFilePath)
        {
            using (var s = new TOM.Server())
            {
                s.Connect(connectionString);
                if (s.Databases.Contains(targetDatabaseID))
                {
                    var db = s.Databases[targetDatabaseID];

                    var dbcontent = TOM.JsonSerializer.SerializeDatabase(db);
                    WriteZip(backupFilePath, dbcontent);
                }
                s.Disconnect();
            }
        }

        public static void WriteZip(string fileName, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fileName));
            using (var fileStream = new FileStream(fileName, FileMode.CreateNew))
            {
                using (var archive = new ZipArchive(fileStream, ZipArchiveMode.Create, false))
                {
                    var entry = archive.CreateEntry("Model.bim");
                    using (var zipStream = entry.Open())
                    {
                        var buffer = Encoding.UTF8.GetBytes(content);
                        zipStream.Write(buffer, 0, buffer.Length);
                    }
                }
            }
        }

        /// <summary>
        /// Deploys the specified database to the specified target server and database ID, using the specified options.
        /// Returns a list of DAX errors (if any) on objects inside the database, in case the deployment was succesful.
        /// </summary>
        /// <param name="db"></param>
        /// <param name="targetConnectionString"></param>
        /// <param name="targetDatabaseID"></param>
        /// <param name="options"></param>
        /// <returns></returns>
        internal static DeploymentResult Deploy(TOM.Database db, string targetConnectionString, string targetDatabaseID, DeploymentOptions options, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(targetConnectionString)) throw new ArgumentNullException("targetConnectionString");
            var s = new TOM.Server();
            s.Connect(targetConnectionString);

            var tmsl = GetTMSL(db, s, targetDatabaseID, options, true);
            cancellationToken.Register(s.CancelCommand);
            var result = s.Execute(tmsl);

            if (result.ContainsErrors)
            {
                throw new Exception(string.Join("\n", result.Cast<XmlaResult>().SelectMany(r => r.Messages.Cast<XmlaMessage>().Select(m => m.Description)).ToArray()));
            }
            
            // Refresh the server object to make sure we get an updated list of databases, in case a new database was made:
            s.Refresh();

            // Fully refresh the deployed database object, to make sure we get updated error messages for the full object tree:
            var deployedDB = s.Databases[targetDatabaseID];
            deployedDB.Refresh(true);
            return
                new DeploymentResult(
                    TabularModelHandler.CheckErrors(deployedDB).Select(t => string.Format("Error on {0}: {1}", GetName(t.Item1), t.Item2)),
                    TabularModelHandler.GetObjectsNotReady(deployedDB).Where(t => t.Item2 == TOM.ObjectState.DependencyError || t.Item2 == TOM.ObjectState.EvaluationError || t.Item2 == TOM.ObjectState.SemanticError)
                        .Select(t => string.Format("Warning! Object not in \"Ready\"-state: {0} ({1})", GetName(t.Item1), t.Item2.ToString())),
                    TabularModelHandler.GetObjectsNotReady(deployedDB).Where(t => t.Item2 == TOM.ObjectState.CalculationNeeded || t.Item2 == TOM.ObjectState.NoData)
                        .Select(t => string.Format("Information: Unprocessed object: {0} ({1})", GetName(t.Item1), t.Item2.ToString()))
                );
        }

        private static string GetName(TOM.NamedMetadataObject obj)
        {
            if (obj is TOM.Hierarchy) return string.Format("hierarchy {0}[{1}]", GetName((obj as TOM.Hierarchy).Table), obj.Name);
            if (obj is TOM.Measure) return string.Format("measure {0}[{1}]", GetName((obj as TOM.Measure).Table), obj.Name);
            if (obj is TOM.Column) return string.Format("column {0}[{1}]", GetName((obj as TOM.Column).Table), obj.Name);
            if (obj is TOM.Partition) return string.Format("partition '{0}' on table {1}", obj.Name, GetName((obj as TOM.Partition).Table));
            if (obj is TOM.Table) return string.Format("'{0}'", obj.Name);
            else return string.Format("{0} '{1}'", ((ObjectType)obj.ObjectType).GetTypeName(), obj.Name);
        }

        /// <summary>
        /// This method transforms a JObject representing a Create TMSL script, so that the database is deployed
        /// using the proper ID and Name values. In addition, of the DeploymentOptions specify that roles should
        /// not be deployed, they are stripped from the TMSL script.
        /// </summary>
        private static JObject TransformCreateTmsl(JObject tmslJObj, string newDbId, DeploymentOptions options)
        {
            tmslJObj["create"]["database"]["id"] = newDbId;
            tmslJObj["create"]["database"]["name"] = newDbId;

            if (!options.DeployRoles)
            {
                // Remove roles if present
                var roles = tmslJObj.SelectToken("create.database.model.roles") as JArray;
                if (roles != null) roles.Clear();
            }

            return tmslJObj;
        }

        private static string DeployNewTMSL(TOM.Database db, string newDbId, DeploymentOptions options, bool includeRestricted)
        {
            var rawTmsl = TOM.JsonScripter.ScriptCreate(db, includeRestricted);

            var jTmsl = JObject.Parse(rawTmsl);

            return TransformCreateTmsl(jTmsl, newDbId, options).ToString();
        }

        /// <summary>
        /// This method transforms a JObject representing a CreateOrReplace TMSL script, so that the script points
        /// to the correct database to be overwritten, and that the correct ID and Name properties are set. In
        /// addition, the method will replace any Roles, RoleMembers, Data Sources and Partitions in the TMSL with
        /// the corresponding TMSL from the specified orgDb, depending on the provided DeploymentOptions.
        /// </summary>
        private static JObject TransformCreateOrReplaceTmsl(JObject tmslJObj, TOM.Database db, TOM.Database orgDb, DeploymentOptions options)
        {
            // Deployment target / existing database (note that TMSL uses the NAME of an existing database, not the ID, to identify the object)
            tmslJObj["createOrReplace"]["object"]["database"] = orgDb.Name;
            tmslJObj["createOrReplace"]["database"]["id"] = orgDb.ID;
            tmslJObj["createOrReplace"]["database"]["name"] = orgDb.Name;

            var model = tmslJObj.SelectToken("createOrReplace.database.model");

            var roles = model["roles"] as JArray;
            if (!options.DeployRoles)
            {
                // Remove roles if present and add original:
                roles = new JArray();
                model["roles"] = roles;
                foreach (var role in orgDb.Model.Roles) roles.Add(JObject.Parse(TOM.JsonSerializer.SerializeObject(role)));
            }
            else if (roles != null && !options.DeployRoleMembers)
            {
                foreach (var role in roles)
                {
                    var members = new JArray();
                    role["members"] = members;

                    // Remove members if present and add original:
                    var roleName = role["name"].Value<string>();
                    if (orgDb.Model.Roles.Contains(roleName))
                    {
                        foreach (var member in orgDb.Model.Roles[roleName].Members)
                            members.Add(JObject.Parse(TOM.JsonSerializer.SerializeObject(member)));
                    }
                }
            }

            if (!options.DeployConnections)
            {
                // Replace existing data sources with those in the target DB:
                // TODO: Can we do anything to retain credentials on PowerQuery data sources?
                var dataSources = model["dataSources"] as JArray;
                foreach (var orgDataSource in orgDb.Model.DataSources)
                {
                    dataSources.FirstOrDefault(d => d.Value<string>("name").EqualsI(orgDataSource.Name))?.Remove();
                    dataSources.Add(JObject.Parse(TOM.JsonSerializer.SerializeObject(orgDataSource)));
                }
            }

            if (!options.DeployPartitions)
            {
                var tables = tmslJObj.SelectToken("createOrReplace.database.model.tables") as JArray;
                foreach (var table in tables)
                {
                    var tableName = table["name"].Value<string>();
                    if (db.Model.Tables[tableName].IsCalculatedOrCalculationGroup()) continue;

                    if (orgDb.Model.Tables.Contains(tableName))
                    {
                        var t = orgDb.Model.Tables[tableName];
                        if (t.IsCalculatedOrCalculationGroup()) continue;

                        var partitions = new JArray();
                        table["partitions"] = partitions;
                        foreach (var pt in t.Partitions) partitions.Add(JObject.Parse(TOM.JsonSerializer.SerializeObject(pt)));
                    }
                }
            }

            return tmslJObj;
        }

        private static JToken GetNamedObj(JToken collection, string objectName)
        {
            if (collection == null) return null;
            return (collection as JArray).FirstOrDefault(t => t.Value<string>("name").EqualsI(objectName));
        }

        private static string DeployExistingTMSL(TOM.Database db, TOM.Server server, string dbId, DeploymentOptions options, bool includeRestricted)
        {
            var orgDb = server.Databases[dbId];
            orgDb.Refresh(true);

            var orgTables = orgDb.Model.Tables;
            var newTables = db.Model.Tables;

            var tmslJson = TOM.JsonScripter.ScriptCreateOrReplace(db, includeRestricted);
            var tmsl = TransformCreateOrReplaceTmsl(JObject.Parse(tmslJson), db, orgDb, options);
            var orgTmsl = tmsl.DeepClone();

            var tmslModel = tmsl["createOrReplace"]["database"]["model"] as JObject;
            bool needsTwoStepCreateOrReplace = false;

            // Detect tables/columns that are change from imported to calculated or vice versa:
            foreach (var newTable in newTables)
            {
                if (!orgTables.ContainsName(newTable.Name)) continue;
                var orgTable = orgTables[newTable.Name];

                // Remove tables that were changed from calculated to imported or vice versa:
                if (orgTable.GetSourceType() != newTable.GetSourceType())
                {
                    GetNamedObj(tmslModel["tables"], newTable.Name).Remove();

                    // Make sure we remove all metadata that points to this table as well
                    // Note, we should be careful not to remove any objects that can hold
                    // processed data:
                    if (tmslModel["perspectives"] != null) foreach (JObject perspective in tmslModel["perspectives"])
                        GetNamedObj(perspective["tables"], newTable.Name)?.Remove();
                    if (tmslModel["cultures"] != null) foreach (JObject culture in tmslModel["cultures"])
                        GetNamedObj(culture["translations"]["model"]["tables"], newTable.Name)?.Remove();
                    if (tmslModel["relationships"] != null) foreach (JObject relationship in tmslModel["relationships"].Where(r => r.Value<string>("fromTable").EqualsI(newTable.Name)
                    || r.Value<string>("toTable").EqualsI(newTable.Name)).ToList())
                        relationship.Remove();
                    if (tmslModel["roles"] != null) foreach (JObject modelRole in tmslModel["roles"])
                        GetNamedObj(modelRole["tablePermissions"], newTable.Name)?.Remove();
                    // Todo: Variants, Alternates, (other objects that can reference a table?)

                    needsTwoStepCreateOrReplace = true;
                    continue;
                }

                foreach (var newColumn in newTable.Columns)
                {
                    if (newColumn.Type == TOM.ColumnType.RowNumber 
                        || newColumn.Type == TOM.ColumnType.CalculatedTableColumn
                        || !orgTable.Columns.ContainsName(newColumn.Name)) continue;
                    var orgColumn = orgTable.Columns[newColumn.Name];

                    // Remove columns that were changed from calculated to data or vice versa:
                    if(orgColumn.Type != newColumn.Type)
                    {
                        var table = GetNamedObj(tmslModel["tables"], newTable.Name);
                        GetNamedObj(table["columns"], newColumn.Name).Remove();

                        // Make sure we remove all references to this column as well:
                        if (tmslModel["perspectives"] != null) foreach (JObject perspective in tmslModel["perspectives"])
                        {
                            var tablePerspective = GetNamedObj(perspective["tables"], newTable.Name);
                            if (tablePerspective == null) continue;
                            GetNamedObj(tablePerspective["columns"], newColumn.Name)?.Remove();
                        }
                        if(tmslModel["cultures"] != null) foreach (JObject culture in tmslModel["cultures"])
                        {
                            var tableTranslation = GetNamedObj(culture["translations"]["model"]["tables"], newTable.Name);
                            if (tableTranslation == null) continue;
                            GetNamedObj(tableTranslation["columns"], newColumn.Name)?.Remove();
                        }
                        if(table["columns"] != null) foreach (JObject column in table["columns"].Where(c => c.Value<string>("sortByColumn").EqualsI(newColumn.Name)))
                        {
                            column["sortByColumn"].Remove();
                        }
                        if (table["hierarchies"] != null) foreach (JObject hierarchy in table["hierarchies"].Where(h => h["levels"].Any(l => l.Value<string>("column").EqualsI(newColumn.Name))).ToList())
                        {
                            hierarchy.Remove();
                        }
                        if (tmslModel["relationships"] != null) foreach (JObject relationship in tmslModel["relationships"].Where(r => r.Value<string>("fromColumn").EqualsI(newColumn.Name)
                        || r.Value<string>("toColumn").EqualsI(newColumn.Name)).ToList())
                        {
                            relationship.Remove();
                        }
                        if (tmslModel["roles"] != null) foreach (JObject modelRole in tmslModel["roles"])
                            GetNamedObj(modelRole["tablePermissions"], newTable.Name)?.Remove();
                        // Todo: Variants, Alternates, (other objects that can reference a column?)

                        needsTwoStepCreateOrReplace = true;
                        continue;
                    }
                }
            }

            if(needsTwoStepCreateOrReplace)
            {
                return new JObject(
                    new JProperty("sequence",
                        new JObject(
                            new JProperty("operations",
                                new JArray(tmsl, orgTmsl))))).ToString();
            }

            return tmsl.ToString();
        }
    }

    public class DeploymentResult
    {
        public readonly IReadOnlyList<string> Issues;
        public readonly IReadOnlyList<string> Warnings;
        public readonly IReadOnlyList<string> Unprocessed;
        public DeploymentResult(IEnumerable<string> issues, IEnumerable<string> warnings, IEnumerable<string> unprocessed)
        {
            Issues = issues.ToList();
            Warnings = warnings.ToList();
            Unprocessed = unprocessed.ToList();
        }
    }
    public class DeploymentOptions
    {
        public DeploymentMode DeployMode = DeploymentMode.CreateOrAlter;
        public bool DeployConnections = false;
        public bool DeployPartitions = false;
        public bool DeployRoles = true;
        public bool DeployRoleMembers = false;

        /// <summary>
        /// Default deployment. Does not overwrite connections, partitions or role members.
        /// </summary>
        public static DeploymentOptions Default = new DeploymentOptions();

        /// <summary>
        /// Full deployment.
        /// </summary>
        public static DeploymentOptions Full = new DeploymentOptions() { DeployConnections = true, DeployPartitions = true, DeployRoles = true, DeployRoleMembers = true };

        /// <summary>
        /// StructureOnly deployment. Does not overwrite roles or role members.
        /// </summary>
        public static DeploymentOptions StructureOnly = new DeploymentOptions() { DeployRoles = false };

        public DeploymentOptions Clone()
        {
            return new DeploymentOptions { DeployMode = this.DeployMode, DeployConnections = this.DeployConnections, DeployPartitions = this.DeployPartitions, DeployRoleMembers = this.DeployRoleMembers, DeployRoles = this.DeployRoles };
        }
    }

    public enum DeploymentMode
    {
        CreateDatabase,
        CreateOrAlter
    }
}
