using System;
using System.Collections.Generic;
using System.Threading;
using Hangfire.Logging;
using Hangfire.Mongo.Database;
using Hangfire.Mongo.Dto;
using Hangfire.Mongo.Signal;
using Hangfire.Mongo.Signal.Mongo;
using Hangfire.Storage;
using MongoDB.Driver;

namespace Hangfire.Mongo.DistributedLock
{
    /// <summary>
    /// Represents distibuted lock implementation for MongoDB
    /// </summary>
    internal sealed class MongoDistributedLock : IDisposable
    {

        private static readonly ILog Logger = LogProvider.For<MongoDistributedLock>();

        private static readonly ThreadLocal<Dictionary<string, int>> AcquiredLocks
                    = new ThreadLocal<Dictionary<string, int>>(() => new Dictionary<string, int>());


        private readonly string _resource;

        private readonly HangfireDbContext _database;

        private readonly MongoStorageOptions _storageOptions;
        private readonly IPersistentSignal _signal;
        private Timer _heartbeatTimer;

        private bool _completed;

        private readonly object _lockObject = new object();


        /// <summary>
        /// Creates MongoDB distributed lock
        /// </summary>
        /// <param name="resource">Lock resource</param>
        /// <param name="timeout">Lock timeout</param>
        /// <param name="database">Lock database</param>
        /// <param name="storageOptions">Database options</param>
        /// <exception cref="DistributedLockTimeoutException">Thrown if lock is not acuired within the timeout</exception>
        /// <exception cref="MongoDistributedLockException">Thrown if other mongo specific issue prevented the lock to be acquired</exception>
        public MongoDistributedLock(string resource, TimeSpan timeout, HangfireDbContext database, MongoStorageOptions storageOptions)
        {
            _resource = resource ?? throw new ArgumentNullException(nameof(resource));
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _storageOptions = storageOptions ?? throw new ArgumentNullException(nameof(storageOptions));

            if (string.IsNullOrEmpty(resource))
            {
                throw new ArgumentException($@"The {nameof(resource)} cannot be empty", nameof(resource));
            }
            if (timeout.TotalSeconds > int.MaxValue)
            {
                throw new ArgumentException($"The timeout specified is too large. Please supply a timeout equal to or less than {int.MaxValue} seconds", nameof(timeout));
            }

            _signal = new MongoSignal(_database.Signal);

            if (!AcquiredLocks.Value.ContainsKey(_resource) || AcquiredLocks.Value[_resource] == 0)
            {
                Cleanup();
                Acquire(timeout);
                AcquiredLocks.Value[_resource] = 1;
                StartHeartBeat();
            }
            else
            {
                AcquiredLocks.Value[_resource]++;
            }
        }


        /// <summary>
        /// Disposes the object
        /// </summary>
        /// <exception cref="MongoDistributedLockException"></exception>
        public void Dispose()
        {
            if (_completed)
            {
                return;
            }
            _completed = true;

            if (!AcquiredLocks.Value.ContainsKey(_resource))
            {
                return;
            }

            AcquiredLocks.Value[_resource]--;

            if (AcquiredLocks.Value[_resource] > 0)
            {
                return;
            }

            // Timer callback may be invoked after the Dispose method call,
            // so we are using lock to avoid unsynchronized calls.
            lock (_lockObject)
            {
                AcquiredLocks.Value.Remove(_resource);

                if (_heartbeatTimer != null)
                {
                    _heartbeatTimer.Dispose();
                    _heartbeatTimer = null;
                }

                Release();

                Cleanup();
            }
        }


        private void Acquire(TimeSpan timeout)
        {
            var isLockAcquired = false;

            try
            {
                var now = DateTime.Now;
                var lockTimeoutTime = now.Add(timeout);

                while (!isLockAcquired && (lockTimeoutTime >= now))
                {
                    isLockAcquired = Acquire();
                    if (!isLockAcquired)
                    {
                        _signal.Wait($@"{nameof(MongoDistributedLock)}.{_resource}", timeout);
                        now = DateTime.Now;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // The signal wait timed out
            }
            catch (Exception ex)
            {
                throw new MongoDistributedLockException($"Could not place a lock on the resource \'{_resource}\': Check inner exception for details.", ex);
            }

            if (!isLockAcquired)
            {
                throw new DistributedLockTimeoutException(
                    $"Could not place a lock on the resource \'{_resource}\': The lock request timed out.");
            }
        }

        /// <summary>
        /// Acquires the lock if possible
        /// </summary>
        /// <returns>
        /// True if lock is acquired else false
        /// </returns>
        private bool Acquire()
        {
            // Acquire the lock if it does not exist - Notice: ReturnDocument.Before
            var filter = Builders<DistributedLockDto>.Filter.Eq(_ => _.Resource, _resource);
            var update = Builders<DistributedLockDto>.Update.SetOnInsert(_ => _.ExpireAt, DateTime.UtcNow.Add(_storageOptions.DistributedLockLifetime));
            var options = new FindOneAndUpdateOptions<DistributedLockDto>
            {
                IsUpsert = true,
                ReturnDocument = ReturnDocument.Before
            };
            var result = _database.DistributedLock.FindOneAndUpdate(filter, update, options);

            return result == null;
        }

        /// <summary>
        /// Release the lock
        /// </summary>
        /// <exception cref="MongoDistributedLockException">Thrown if releasing the lock fails</exception>
        private void Release()
        {
            try
            {
                // Remove resource lock
                _database.DistributedLock.DeleteOne(
                    Builders<DistributedLockDto>.Filter.Eq(_ => _.Resource, _resource));

                _signal.Set($@"{nameof(MongoDistributedLock)}.{_resource}");
            }
            catch (Exception ex)
            {
                throw new MongoDistributedLockException($"Could not release a lock on the resource \'{_resource}\': Check inner exception for details.", ex);
            }
        }


        private void Cleanup()
        {
            try
            {
                // Delete expired locks
                _database.DistributedLock.DeleteMany(
                    Builders<DistributedLockDto>.Filter.Eq(_ => _.Resource, _resource) &
                    Builders<DistributedLockDto>.Filter.Lt(_ => _.ExpireAt, DateTime.UtcNow));
            }
            catch (Exception ex)
            {
                Logger.ErrorFormat("Unable to clean up locks on the resource '{0}'. {1}", _resource, ex);
            }
        }

        /// <summary>
        /// Starts database heartbeat
        /// </summary>
        private void StartHeartBeat()
        {
            TimeSpan timerInterval = TimeSpan.FromMilliseconds(_storageOptions.DistributedLockLifetime.TotalMilliseconds / 5);

            _heartbeatTimer = new Timer(state =>
            {
                // Timer callback may be invoked after the Dispose method call,
                // so we are using lock to avoid unsynchronized calls.
                lock (_lockObject)
                {
                    try
                    {
                        var filter = Builders<DistributedLockDto>.Filter.Eq(_ => _.Resource, _resource);
                        var update = Builders<DistributedLockDto>.Update.Set(_ => _.ExpireAt, DateTime.UtcNow.Add(_storageOptions.DistributedLockLifetime));
                        _database.DistributedLock.FindOneAndUpdate(filter, update);
                    }
                    catch (Exception ex)
                    {
                        Logger.ErrorFormat("Unable to update heartbeat on the resource '{0}'. {1}", _resource, ex);
                    }
                }
            }, null, timerInterval, timerInterval);
        }

    }
}
