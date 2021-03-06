﻿using System;
using System.Linq;
using System.Reflection;
using Hangfire.Mongo.Database;
using Hangfire.Mongo.DistributedLock;
using Hangfire.Mongo.Migration.Strategies;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Hangfire.Mongo.Migration
{

    /// <summary>
    /// Manages migration from one schema version to the required.
    /// </summary>
    internal class MongoMigrationManager
    {
        private readonly MongoStorageOptions _storageOptions;

        public static MongoSchema RequiredSchemaVersion => Enum.GetValues(typeof(MongoSchema)).Cast<MongoSchema>().OrderBy(v => v).Last();

        public MongoMigrationManager(MongoStorageOptions storageOptions)
        {
            _storageOptions = storageOptions ?? throw new ArgumentNullException(nameof(storageOptions));
        }

        public void Migrate(HangfireDbContext dbContext)
        {
            using (new MongoDistributedLock(nameof(Migrate), TimeSpan.FromSeconds(30), dbContext, _storageOptions))
            {
                var currentSchema = dbContext.Schema.Find(new BsonDocument()).FirstOrDefault();
                if (currentSchema == null)
                {
                    // We do not have a schema version yet
                    // - assume an empty connection and run full migrations
                    var migrationRunner = new MongoMigrationRunner(dbContext, _storageOptions);
                    migrationRunner.Execute(MongoSchema.None, RequiredSchemaVersion);
                    return;
                }

                var currentSchemaVersion = (MongoSchema)currentSchema.Version;
                if (RequiredSchemaVersion < currentSchemaVersion)
                {
                    var assemblyName = GetType().GetTypeInfo().Assembly.GetName();
                    throw new InvalidOperationException(
                        $"{Environment.NewLine}{assemblyName.Name} version: {assemblyName.Version}, uses a schema prior to the current connection." +
                        $"{Environment.NewLine}Backwards migration is not supported. Please resolve this manually (e.g. by droping the connection)." +
                        $"{Environment.NewLine}Please see https://github.com/sergeyzwezdin/Hangfire.Mongo#migration for further information.");
                }

                if (RequiredSchemaVersion == currentSchemaVersion)
                {
                    // Nothing to migrate - so let's get outa here.
                    return;
                }

                IMongoMigrationStrategy migration;
                switch (_storageOptions.MigrationOptions.Strategy)
                {
                    case MongoMigrationStrategy.None:
                        migration = new MongoMigrationStrategyNone();
                        break;
                    case MongoMigrationStrategy.Drop:
                        migration = new MongoMigrationStrategyDrop(dbContext, _storageOptions);
                        break;
                    case MongoMigrationStrategy.Migrate:
                        migration = new MongoMigrationStrategyMigrate(dbContext, _storageOptions);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException($@"Unknown migration strategy: {_storageOptions.MigrationOptions.Strategy}", $@"{nameof(MongoMigrationOptions)}.{nameof(MongoMigrationOptions.Strategy)}");
                }
                migration.Migrate(currentSchemaVersion, RequiredSchemaVersion);
            }
        }


    }
}
