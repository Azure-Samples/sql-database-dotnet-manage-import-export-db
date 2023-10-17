// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager.Samples.Common;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager;
using Azure.ResourceManager.Models;
using Azure.ResourceManager.Sql;
using Azure.ResourceManager.Sql.Models;
using Azure.ResourceManager.Storage.Models;
using Azure.ResourceManager.Storage;
using System.Collections.Generic;
using System.Threading.Tasks;
using System;

namespace ManageSqlImportExportDatabase
{
    public class Program
    {
        private static ResourceIdentifier? _resourceGroupId = null;

        /**
         * Azure SQL sample for managing import/export SQL Database -
         *  - Create a SQL Server with one database from a pre-existing sample.
         *  - Create a storage account and export a database
         *  - Create a new database from a backup using the import functionality
         *  - Update an empty database with a backup database using the import functionality
         *  - Delete storage account, databases and SQL Server
         */
        public static async Task RunSample(ArmClient client)
        {
            try
            {
                //Get default subscription
                SubscriptionResource subscription = await client.GetDefaultSubscriptionAsync();

                //Create a resource group in the EastUS region
                string rgName = Utilities.CreateRandomName("rgSQLServer");
                Utilities.Log("Creating resource group...");
                var rgLro = await subscription.GetResourceGroups().CreateOrUpdateAsync(WaitUntil.Completed, rgName, new ResourceGroupData(AzureLocation.EastUS));
                ResourceGroupResource resourceGroup = rgLro.Value;
                _resourceGroupId = resourceGroup.Id;
                Utilities.Log($"Created a resource group with name: {resourceGroup.Data.Name} ");

                // ============================================================
                // Create a SQL Server with one database from a sample.
                string sqlServerName = Utilities.CreateRandomName("sqlserver");
                Utilities.Log("Creating SQL Server...");
                string sqlAdmin = "sqladmin" + sqlServerName;
                string sqlAdminPwd = Utilities.CreatePassword();
                SqlServerData sqlData = new SqlServerData(AzureLocation.EastUS)
                {
                    AdministratorLogin = sqlAdmin,
                    AdministratorLoginPassword = sqlAdminPwd,
                    PublicNetworkAccess = ServerNetworkAccessFlag.Enabled
                };
                var sqlServer =(await resourceGroup.GetSqlServers().CreateOrUpdateAsync(WaitUntil.Completed, sqlServerName, sqlData)).Value;
                Utilities.Log($"Created a SQL Server with name: {sqlServer.Data.Name} ");

                string firewallRuleName = Utilities.CreateRandomName("firewallrule-");
                SqlFirewallRuleData firewallRuleData = new SqlFirewallRuleData()
                {
                    StartIPAddress = "10.0.0.1",
                    EndIPAddress = "10.0.0.10"
                };
                await sqlServer.GetSqlFirewallRules().CreateOrUpdateAsync(WaitUntil.Completed, firewallRuleName, firewallRuleData);

                Utilities.Log("Creating a database...");
                string dbFromSampleName = Utilities.CreateRandomName("db-from-sample");
                SqlDatabaseData dbFromSampleData = new SqlDatabaseData(AzureLocation.EastUS)
                {
                    Sku = new SqlSku("Basic"),
                    SampleName = SampleSchemaName.AdventureWorksLT
                };
                var dbFromSample = (await sqlServer.GetSqlDatabases().CreateOrUpdateAsync(WaitUntil.Completed, dbFromSampleName, dbFromSampleData)).Value;
                Utilities.Log($"Created database with name: {dbFromSample.Data.Name} ");

                // ============================================================
                // Export a database from a SQL server created above to a new storage account within the same resource group.
                Utilities.Log("Creating a new storage account in the same resource group...");
                string storageAccountName = Utilities.CreateRandomName("sqlserverst");
                StorageAccountCreateOrUpdateContent storageAccountData = new StorageAccountCreateOrUpdateContent(new StorageSku(StorageSkuName.StandardGrs), StorageKind.Storage, AzureLocation.EastUS);
                var storageAccountLro = await resourceGroup.GetStorageAccounts().CreateOrUpdateAsync(WaitUntil.Completed,storageAccountName,storageAccountData);
                StorageAccountResource storageAccount = storageAccountLro.Value;
                Utilities.Log($"Created a new storage account with name: {storageAccount.Data.Name}");
                //Create Storage Blob
                var blobName = Utilities.CreateRandomName("exportcontainer");
                BlobContainerData blobData = new BlobContainerData()
                {
                    PublicAccess = StoragePublicAccessType.Container
                };
                var storageBlob = (await storageAccount.GetBlobService().GetBlobContainers().CreateOrUpdateAsync(WaitUntil.Completed, blobName,blobData)).Value;
                var storageAccountKey = new List<string>();
                await foreach (var item in storageAccount.GetKeysAsync())
                {
                    storageAccountKey.Add(item.Value);
                }
                string fileName = "dbfromsample.bacpac";
                string primaryEndpoint = $"https://{storageAccount.Data.Name}.blob.core.windows.net/{storageBlob.Data.Name}/{fileName}";
                Utilities.Log($"Created export url : {primaryEndpoint}");

                Utilities.Log("Exporting a database from a SQL server created above to a new storage account within the same resource group...");
                var exporteData = new DatabaseExportDefinition(
                    StorageKeyType.StorageAccessKey,
                    storageAccountKey[0].Trim(),
                    new(primaryEndpoint),
                    sqlAdmin,
                    sqlAdminPwd
                    );
                //When exporting or importing for the first time, you need to enter the portal and manually click Allow Azure services and resources to access this server.
                var exportedDB = await dbFromSample.ExportAsync(WaitUntil.Completed, exporteData);
                Utilities.Log($"Export success with name: {exportedDB.Value.Name}");
                // ============================================================
                // Import a database within a new elastic pool from a storage account container created above.
                Utilities.Log("Creating a new elastic pool...");
                string elasticPoolName = Utilities.CreateRandomName("epi");
                var elasticPooldata = new ElasticPoolData(AzureLocation.EastUS)
                {
                    Sku = new SqlSku("StandardPool")
                };
                var elasticPool = (await sqlServer.GetElasticPools().CreateOrUpdateAsync(WaitUntil.Completed, elasticPoolName, elasticPooldata)).Value;

                Utilities.Log("Importing a database within a new elastic pool from a storage account container created above....");

                string dbFromImportName = Utilities.CreateRandomName("db-from-import1");
                var importData = new DatabaseImportDefinition(StorageKeyType.StorageAccessKey, storageAccountKey[0], new(primaryEndpoint), sqlAdmin, sqlAdminPwd)
                {
                    DatabaseName = dbFromImportName,
                    Edition = "Basic"
                };
                var importDB = (await sqlServer.ImportDatabaseAsync(WaitUntil.Completed, importData)).Value;
                var dbFromImport = (await sqlServer.GetSqlDatabaseAsync(dbFromImportName)).Value;
                var dbFromData = new SqlDatabasePatch()
                {
                    ElasticPoolId = elasticPool.Data.Id
                };
                dbFromImport = (await dbFromImport.UpdateAsync(WaitUntil.Completed,dbFromData)).Value;
                Utilities.Log($"Import success with name: {importDB.Name}");

                // Delete the database.
                Utilities.Log("Deleting a database");
                await dbFromImport.DeleteAsync(WaitUntil.Completed);

                // ============================================================
                // Create an empty database within an elastic pool.
                string dbEmptyName = Utilities.CreateRandomName("db-from-import2");
                SqlDatabaseData dbEmptyData = new SqlDatabaseData(AzureLocation.EastUS)
                {
                    ElasticPoolId = elasticPool.Id
                };
                SqlDatabaseResource dbEmpty = (await sqlServer.GetSqlDatabases().CreateOrUpdateAsync(WaitUntil.Completed, dbEmptyName, dbEmptyData)).Value;

                // ============================================================
                // Import data from a BACPAC to an empty database within an elastic pool.
                Utilities.Log("Importing data from a BACPAC to an empty database within an elastic pool....");

                var importEmptyData = new ImportExistingDatabaseDefinition(
                    StorageKeyType.StorageAccessKey,
                    storageAccountKey[0], 
                    new(primaryEndpoint), 
                    sqlAdmin, 
                    sqlAdminPwd
                    );
                var importEmpty = (await dbEmpty.ImportAsync(WaitUntil.Completed,importEmptyData)).Value;
                Utilities.Log($"Importing data with name : {importEmpty.Name}");

                // Delete the storage account.
                Utilities.Log("Deleting the storage account");
                await storageAccount.DeleteAsync(WaitUntil.Completed);

                // Delete the databases.
                Utilities.Log("Deleting the databases");
                await dbEmpty.DeleteAsync(WaitUntil.Completed);
                await dbFromSample.DeleteAsync(WaitUntil.Completed);

                // Delete the elastic pool.
                Utilities.Log("Deleting the elastic pool");
                await elasticPool.DeleteAsync(WaitUntil.Completed);

                // Delete the SQL Server.
                Utilities.Log("Deleting a Sql Server");
                await sqlServer.DeleteAsync(WaitUntil.Completed);

            }
            finally
            {
                try
                {
                    if (_resourceGroupId is not null)
                    {
                        Utilities.Log($"Deleting Resource Group...");
                        await client.GetResourceGroupResource(_resourceGroupId).DeleteAsync(WaitUntil.Completed);
                        Utilities.Log($"Deleted Resource Group: {_resourceGroupId.Name}");
                    }
                }
                catch (Exception e)
                {
                    Utilities.Log(e);
                }
            }
        }
        public static async Task Main(string[] args)
        {
            try
            {
                //=================================================================
                // Authenticate

                var clientId = Environment.GetEnvironmentVariable("CLIENT_ID");
                var clientSecret = Environment.GetEnvironmentVariable("CLIENT_SECRET");
                var tenantId = Environment.GetEnvironmentVariable("TENANT_ID");
                var subscription = Environment.GetEnvironmentVariable("SUBSCRIPTION_ID");
                ClientSecretCredential credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
                ArmClient client = new ArmClient(credential, subscription);

                await RunSample(client);
            }
            catch (Exception e)
            {
                Utilities.Log(e);
            }
        }
    }
}