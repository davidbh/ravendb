﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FastTests.Issues;
using FastTests.Server.Documents.Replication;
using Raven.Abstractions.Connection;
using Raven.Abstractions.Exceptions;
using Raven.Client.Indexes;
using Raven.Server.Documents;
using Raven.Server.Utils;
using Xunit;

namespace FastTests.Server.Replication
{
    public class ReplicationIndexesAndTransformers : ReplicationTestsBase
    {
        public class User
        {
            public string Name { get; set; }
            public int Age { get; set; }
            public DateTime Birthday { get; set; }
        }

        public class UserByNameAndBirthday : AbstractIndexCreationTask<User>
        {
            public UserByNameAndBirthday()
            {
                Map = users => from user in users
                    select new
                    {
                        user.Name,
                        user.Birthday
                    };
            }
        }


        public class UserByNameIndex : AbstractIndexCreationTask<User>
        {
            private readonly string _indexName;

            public override string IndexName =>
                string.IsNullOrEmpty(_indexName) ? base.IndexName : _indexName;


            public UserByNameIndex(string name = null)
            {
                _indexName = name;
                Map = users => from user in users
                    select new
                    {
                        user.Name
                    };
            }
        }

        public class UserByAgeIndex : AbstractIndexCreationTask<User>
        {
            private readonly string _indexName;

            public override string IndexName =>
                string.IsNullOrEmpty(_indexName) ? base.IndexName : _indexName;

            public UserByAgeIndex(string name = null)
            {
                _indexName = name;
                Map = users => from user in users
                    select new
                    {
                        user.Age
                    };
            }
        }

        public class UsernameToUpperTransformer : AbstractTransformerCreationTask<User>
        {
            private readonly string _transformerName;

            public override string TransformerName =>
                string.IsNullOrEmpty(_transformerName) ? base.TransformerName : _transformerName;

            public UsernameToUpperTransformer(string transformerName = null)
            {
                _transformerName = transformerName;
                TransformResults = users => from user in users
                    select new
                    {
                        Name = user.Name.ToUpper(),
                        user.Age,
                        user.Birthday
                    };
            }
        }

        [Fact]
        public async Task Deleting_indexes_should_delete_relevant_metadata()
        {
            using (var store = GetDocumentStore())
            {
                var userByAge = new UserByAgeIndex();
                var userByName = new UserByNameIndex();
                userByName.Execute(store);
                userByAge.Execute(store);

                var databaseStore =
                    await Server.ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(store.DefaultDatabase);

                store.DatabaseCommands.DeleteIndex(userByName.IndexName);

                var metadata = databaseStore.IndexMetadataPersistence.GetIndexMetadataByName(userByAge.IndexName);
                Assert.NotNull(metadata);

                metadata = databaseStore.IndexMetadataPersistence.GetIndexMetadataByName(userByName.IndexName);
                Assert.Null(metadata);

                store.DatabaseCommands.DeleteIndex(userByAge.IndexName);
                metadata = databaseStore.IndexMetadataPersistence.GetIndexMetadataByName(userByAge.IndexName);
                Assert.Null(metadata);
            }
        }

        [Fact]
        public async Task PurgeTombstonesFrom_should_work_properly()
        {
            using (var store = GetDocumentStore())
            {
                var userByAge = new UserByAgeIndex();
                var userByName = new UserByNameIndex();
                var userByNameAndBirthday = new UserByNameAndBirthday();
                userByName.Execute(store);
                userByAge.Execute(store);
                userByNameAndBirthday.Execute(store);

                store.DatabaseCommands.DeleteIndex(userByName.IndexName);
                store.DatabaseCommands.DeleteIndex(userByNameAndBirthday.IndexName);

                var databaseStore =
                    await Server.ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(store.DefaultDatabase);
                databaseStore.IndexMetadataPersistence.PurgeTombstonesFrom(0, 1024);

                var metadataCollection = databaseStore.IndexMetadataPersistence.GetAfter(0, 0, 1024);
                Assert.Equal(1, metadataCollection.Count);
                Assert.Equal(userByAge.IndexName.ToLower(), metadataCollection[0].Name);
            }
        }

        [Fact]
        public async Task GetAfter_should_work_properly()
        {
            using (var store = GetDocumentStore())
            {
                var userByAge = new UserByAgeIndex();
                var userByName = new UserByNameIndex();
                var userByNameAndBirthday = new UserByNameAndBirthday();
                userByName.Execute(store);
                userByAge.Execute(store);
                userByNameAndBirthday.Execute(store);

                var databaseStore =
                    await Server.ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(store.DefaultDatabase);
                var metadataItems = databaseStore.IndexMetadataPersistence.GetAfter(0, 0, 1024);
                Assert.Equal(3, metadataItems.Count);

                metadataItems = databaseStore.IndexMetadataPersistence.GetAfter(3, 0, 1024);
                Assert.Equal(1, metadataItems.Count);

                //this one was created last, so it has the largest etag
                Assert.Equal(userByNameAndBirthday.IndexName.ToLower(), metadataItems[0].Name);

                store.DatabaseCommands.DeleteIndex(userByName.IndexName);
                store.DatabaseCommands.DeleteIndex(userByNameAndBirthday.IndexName);

                metadataItems = databaseStore.IndexMetadataPersistence.GetAfter(0, 0, 1024);
                Assert.Equal(3, metadataItems.Count); //together with tombstones
                Assert.Equal(2, metadataItems.Count(item => item.Id == -1));
            }
        }

        [Fact]
        public async Task Deleting_indexes_should_write_tombstones()
        {
            using (var store = GetDocumentStore())
            {
                var userByAge = new UserByAgeIndex();
                var userByName = new UserByNameIndex();
                userByName.Execute(store);
                userByAge.Execute(store);

                store.DatabaseCommands.DeleteIndex(userByName.IndexName);
                store.DatabaseCommands.DeleteIndex(userByAge.IndexName);
                var databaseStore =
                    await Server.ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(store.DefaultDatabase);

                var metadata = databaseStore.IndexMetadataPersistence.GetIndexMetadataByName(userByAge.IndexName,
                    returnNullIfTombstone: false);
                Assert.NotNull(metadata);
                Assert.Equal(-1, metadata.Id);
                Assert.Equal(userByAge.IndexName.ToLower(), metadata.Name);

                metadata = databaseStore.IndexMetadataPersistence.GetIndexMetadataByName(userByName.IndexName,
                    returnNullIfTombstone: false);
                Assert.NotNull(metadata);
                Assert.Equal(-1, metadata.Id);
                Assert.Equal(userByName.IndexName.ToLower(), metadata.Name);

            }
        }

        [Fact]
        public async Task Creating_indexes_should_create_relevant_metadata()
        {
            using (var store = GetDocumentStore())
            {
                var userByAge = new UserByAgeIndex();
                var userByName = new UserByNameIndex();
                userByName.Execute(store);
                userByAge.Execute(store);

                var databaseStore =
                    await Server.ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(store.DefaultDatabase);

                var metadataByName = databaseStore.IndexMetadataPersistence.GetIndexMetadataByName(userByName.IndexName);
                Assert.NotNull(metadataByName);

                var serversideIndexMetadata = databaseStore.IndexStore.GetIndex(userByName.IndexName);
                Assert.Equal(serversideIndexMetadata.IndexId, metadataByName.Id);

                var metadataByAge = databaseStore.IndexMetadataPersistence.GetIndexMetadataByName(userByAge.IndexName);
                Assert.NotNull(metadataByAge);

                serversideIndexMetadata = databaseStore.IndexStore.GetIndex(userByAge.IndexName);
                Assert.Equal(serversideIndexMetadata.IndexId, metadataByAge.Id);
            }
        }

        //An index can't be named with the same name as transformer or vice versa
        [Fact]
        public void Index_and_transformer_metadata_storage_should_enforce_name_uniqueness_for_writing_index_then_transformer()
        {
            using (var store = GetDocumentStore())
            {
                var userByNameIndex = new UserByNameIndex("FooBar");
                userByNameIndex.Execute(store);

                var usernameToUpperTransformer = new UsernameToUpperTransformer("FooBar");
                Assert.Throws<ErrorResponseException>(() => usernameToUpperTransformer.Execute(store));
            }
        }

        [Fact]
        public void Index_and_transformer_metadata_storage_should_enforce_name_uniqueness_for_writing_transformer_then_index()
        {
            using (var store = GetDocumentStore())
            {
                var usernameToUpperTransformer = new UsernameToUpperTransformer("FooBar");
                usernameToUpperTransformer.Execute(store);

                var userByNameIndex = new UserByNameIndex("FooBar");
                Assert.Throws<ErrorResponseException>(() => userByNameIndex.Execute(store));
            }
        }

        [Fact]
        public async Task New_index_will_overwrite_transformer_tombstones()
        {
            using (var store = GetDocumentStore())
            {
                const string name = "FooBar";
                var usernameToUpperTransformer = new UsernameToUpperTransformer(name);
                usernameToUpperTransformer.Execute(store);

                store.DatabaseCommands.DeleteTransformer(name);

                var userByNameIndex = new UserByNameIndex(name);
                userByNameIndex.Execute(store);

                var databaseStore =
                    await Server.ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(store.DefaultDatabase);

                var metadata = databaseStore.IndexMetadataPersistence.GetIndexMetadataByName(name);
                Assert.Equal(1, metadata.ChangeVector.Length); //sanity check

                /*
                 transformers and etags share the same tombstone,
                 so if transformer created, then deleted, then index created under the same name as transformer, the change vector
                 which represents history of the object will be preserved
                */
                Assert.Equal(3, metadata.ChangeVector[0].Etag); 
            }
        }

        [Fact]
        public async Task New_transformer_will_overwrite_index_tombstones()
        {
            using (var store = GetDocumentStore())
            {
                const string name = "FooBar";
                var userByNameIndex = new UserByNameIndex(name);
                userByNameIndex.Execute(store);

                store.DatabaseCommands.DeleteIndex(name);

                var usernameToUpperTransformer = new UsernameToUpperTransformer(name);
                usernameToUpperTransformer.Execute(store);

                var databaseStore =
                    await Server.ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(store.DefaultDatabase);

                var metadata = databaseStore.IndexMetadataPersistence.GetIndexMetadataByName(name);
                Assert.Equal(1, metadata.ChangeVector.Length); //sanity check

                /*
                 transformers and etags share the same tombstone,
                 so if transformer created, then deleted, then index created under the same name as transformer, the change vector
                 which represents history of the object will be preserved
                */
                Assert.Equal(3, metadata.ChangeVector[0].Etag);
            }
        }

        [Fact]
        public async Task Manually_removed_indexes_would_remove_metadata_on_startup()
        {
            var pathPrefix = Guid.NewGuid().ToString();
            var databasePath = String.Empty;
            var indexesPath = String.Empty;
            
            try
            {
                var userByAgeIndex = new UserByAgeIndex();
                var userByNameIndex = new UserByNameIndex();
                var usernameToUpperTransformer = new UsernameToUpperTransformer();


                DocumentDatabase databaseStore;
                using (var store = GetDocumentStore(path: pathPrefix))
                {
                    userByNameIndex.Execute(store);
                    userByAgeIndex.Execute(store);
                    usernameToUpperTransformer.Execute(store);
                    databaseStore = await Server.ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(store.DefaultDatabase);

                    Assert.NotNull(databaseStore.IndexMetadataPersistence.GetIndexMetadataByName(userByNameIndex.IndexName));
                    Assert.NotNull(databaseStore.IndexMetadataPersistence.GetIndexMetadataByName(userByAgeIndex.IndexName));
                    Assert.NotNull(databaseStore.IndexMetadataPersistence.GetIndexMetadataByName(usernameToUpperTransformer.TransformerName));
                }

                indexesPath = databaseStore.Configuration.Indexing.IndexStoragePath;
                databasePath = databaseStore.Configuration.Core.DataDirectory;
                foreach (var indexFolder in Directory.GetDirectories(indexesPath))
                    IOExtensions.DeleteDirectory(indexFolder);

                using (var store = GetDocumentStore(path: pathPrefix))
                {
                    databaseStore = await Server.ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(store.DefaultDatabase);

                    Assert.Null(databaseStore.IndexMetadataPersistence.GetIndexMetadataByName(userByNameIndex.IndexName));
                    Assert.Null(databaseStore.IndexMetadataPersistence.GetIndexMetadataByName(userByAgeIndex.IndexName));
                    Assert.Null(databaseStore.IndexMetadataPersistence.GetIndexMetadataByName(usernameToUpperTransformer.TransformerName));
                }
            }
            finally
            {
                IOExtensions.DeleteDirectory(databasePath);
                IOExtensions.DeleteDirectory(indexesPath);
            }
        }
    }
}

       
