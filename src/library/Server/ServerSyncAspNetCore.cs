﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UpdateServices.WebServices.ServerSync;
using Microsoft.UpdateServices.Storage;
using System.Linq;
using Microsoft.UpdateServices.Metadata;
using Microsoft.UpdateServices.Metadata.Content;
using System.IO;
using Microsoft.UpdateServices.Metadata.Prerequisites;
using Microsoft.AspNetCore.Builder;

namespace Microsoft.UpdateServices.Server
{
    /// <summary>
    /// Upstream update server implementation. Provides updates over the ServerSync protocol to downstream servers.
    /// </summary>
    class ServerSyncWebService : IServerSyncAspNetCore
    {
        /// <summary>
        /// The source of upate metadata that this server serves.
        /// </summary>
        private readonly IMetadataSource MetadataSource;

        /// <summary>
        /// Cached service configuration
        /// </summary>
        private readonly ServerSyncConfigData ServiceConfiguration;

        private readonly MetadataFilter UpdatesFilter;

        private readonly Dictionary<Guid, List<Update>> ProductsIndex;
        private readonly Dictionary<Guid, List<Update>> ClassificationsIndex;
        private readonly Dictionary<Identity, Update> FilteredUpdates;

        private readonly ICollection<Update> Categories;

        /// <summary>
        /// Instantiate the server and serve updates from the local repo
        /// </summary>
        /// <param name="metadataSource">The update metadata source to serve updates from</param>
        /// <param name="filter">The filter for which updates to serve.</param>
        /// <param name="serviceConfig">Service configuration.</param>
        public ServerSyncWebService(IMetadataSource metadataSource, MetadataFilter filter, ServerSyncConfigData serviceConfig)
        {
            MetadataSource = metadataSource;
            ServiceConfiguration = serviceConfig;

            UpdatesFilter = filter;

            Categories = metadataSource.GetCategories();
            FilteredUpdates = metadataSource.GetUpdates(filter).ToDictionary(u => u.Identity);

            // If an update contains bundled updates, those bundled updates must also be made available to downstream servers
            var bundledUpdates = FilteredUpdates.Values.Where(u => u.IsBundle).SelectMany(u => u.BundledUpdates).Distinct().ToList();
            foreach(var bundledUpdateId in bundledUpdates)
            {
                if (!FilteredUpdates.ContainsKey(bundledUpdateId))
                {
                    FilteredUpdates.Add(bundledUpdateId, metadataSource.GetUpdate(bundledUpdateId));
                }
            }

            // Build the lookup tables by products and classifications
            ProductsIndex = new Dictionary<Guid, List<Update>>();
            ClassificationsIndex = new Dictionary<Guid, List<Update>>();

            foreach (var update in FilteredUpdates.Values)
            {
                if (update.HasProduct)
                {
                    foreach (var productId in update.ProductIds)
                    {
                        if (!ProductsIndex.ContainsKey(productId))
                        {
                            ProductsIndex[productId] = new List<Update>();
                        }

                        ProductsIndex[productId].Add(update);
                    }
                }
                
                if (update.HasClassification)
                {
                    foreach (var classificationId in update.ClassificationIds)
                    {
                        if (!ClassificationsIndex.ContainsKey(classificationId))
                        {
                            ClassificationsIndex[classificationId] = new List<Update>();
                        }

                        ClassificationsIndex[classificationId].Add(update);
                    }
                }
            }
        }

        /// <summary>
        /// Handle authentication data requests
        /// </summary>
        /// <param name="request">The request data. Not used</param>
        /// <returns>Exactly one canned authentication method</returns>
        public Task<ServerAuthConfig> GetAuthConfigAsync(GetAuthConfigRequest request)
        {
            // Build the standard response
            var result = new ServerAuthConfig()
            {
                LastChange = DateTime.Now,
                AuthInfo = new AuthPlugInInfo[]
                {
                    new AuthPlugInInfo() { PlugInID = "DssTargeting", ServiceUrl = "DssAuthWebService/DssAuthWebService.asmx" } 
                }
            };

            GetAuthConfigResponse response = new GetAuthConfigResponse(new GetAuthConfigResponseBody() { GetAuthConfigResult = result });

            return Task.FromResult(response.GetAuthConfigResponse1.GetAuthConfigResult);
        }

        /// <summary>
        /// Handle service configuration requests
        /// </summary>
        /// <param name="request">Service configuration request</param>
        /// <returns>Returns the cached service configuration of the upstream server the local repo is tracking</returns>
        public Task<ServerSyncConfigData> GetConfigDataAsync(GetConfigDataRequest request)
        {
            return Task.FromResult(ServiceConfiguration);
        }

        /// <summary>
        /// Handle request for a cookie
        /// </summary>
        /// <param name="request">Cookie request. Not used; all requests are granted</param>
        /// <returns>A cookie that expires in 5 days.</returns>
        public Task<Cookie> GetCookieAsync(GetCookieRequest request)
        {
            return Task.FromResult(new Cookie() { Expiration = DateTime.Now.AddDays(5), EncryptedData = new byte[12] });
        }

        /// <summary>
        /// Return a list of update ids
        /// </summary>
        /// <param name="request">Request data. Can specify categories or updates, filters, etc.</param>
        /// <returns></returns>
        public Task<RevisionIdList> GetRevisionIdListAsync(GetRevisionIdListRequest request)
        {
            var response = new RevisionIdList();
            response.Anchor = DateTime.Now.ToString();

            if (request.GetRevisionIdList.filter.GetConfig == true)
            {
                if (!string.IsNullOrEmpty(request.GetRevisionIdList.filter.Anchor) && DateTime.TryParse(request.GetRevisionIdList.filter.Anchor, out DateTime anchorTime))
                {
                    // If we have an anchor, return only categories that have changed after the anchor
                    response.NewRevisions = Categories
                        .Select(u => u.Identity.Raw)
                        .ToArray();
                }
                else
                {
                    // else return all categories
                    response.NewRevisions = Categories.Select(u => u.Identity.Raw).ToArray();
                }
            }
            else
            {
                var productsFilter = request.GetRevisionIdList.filter.Categories;
                var classificationsFilter = request.GetRevisionIdList.filter.Classifications;

                var requestedUpdateIds = new List<Update>();
                
                if (productsFilter != null)
                {
                    // Apply the product filter
                    foreach (var product in productsFilter)
                    {
                        if (ProductsIndex.ContainsKey(product.Id))
                        {
                            requestedUpdateIds.AddRange(ProductsIndex[product.Id]);
                        }
                    }
                }
                else
                {
                    requestedUpdateIds.AddRange(FilteredUpdates.Values);
                }
                
                if (classificationsFilter != null)
                {
                    var classificationFilterIds = classificationsFilter.Select(filter => filter.Id).ToList();

                    // Remove all updates that don't have classifications
                    requestedUpdateIds.RemoveAll(u => !u.HasClassification);

                    // Remove all updates that don't have a classification that matches the filter
                    requestedUpdateIds.RemoveAll(
                        u => !u.ClassificationIds.Any(c => classificationFilterIds.Contains(c)));
                }

                // Deduplicate result and convert to raw identity format
                response.NewRevisions = requestedUpdateIds.GroupBy(u => u.Identity).Select(g => g.Key).Select(k => k.Raw).ToArray();
            }

            return Task.FromResult(response);
        }

        /// <summary>
        /// Return metadata for updates
        /// </summary>
        /// <param name="request">The request; contains IDs for updates to retrieve metadata for</param>
        /// <returns>Update metadata for requested updates</returns>
        public Task<ServerUpdateData> GetUpdateDataAsync(GetUpdateDataRequest request)
        {
            var response = new ServerUpdateData();

            // Make sure the request is not larger than the config says
            var updateRequestCount = request.GetUpdateData.updateIds.Count();
            if (updateRequestCount > ServiceConfiguration.MaxNumberOfUpdatesPerRequest)
            {
                return null;
            }

            var returnUpdatesList = new List<ServerSyncUpdateData>();
            var returnFilesList = new List<ServerSyncUrlData>();
            foreach (var rawIdentity in request.GetUpdateData.updateIds)
            {
                var updateIdentity = new Identity(rawIdentity);

                // Find the update; it can be either category or update
                Update update;
                if (!FilteredUpdates.TryGetValue(updateIdentity, out update))
                {
                    if ((update = Categories.First(c => c.Identity.Equals(updateIdentity))) == null)
                    {
                        throw new Exception("Update not found");
                    }
                }

                if (update.HasFiles)
                {
                    // if update contains files, we must also gather file information
                    foreach (var updateFile in update.Files)
                    {
                        returnFilesList.Add(
                            new ServerSyncUrlData()
                            {
                                FileDigest = Convert.FromBase64String(updateFile.Digests[0].DigestBase64),
                                MUUrl = updateFile.Urls[0].MuUrl,
                                UssUrl = $"Content/{updateFile.GetContentDirectoryName()}/{updateFile.FileName}"
                            });
                    }
                }

                var rawUpdateData = new ServerSyncUpdateData();
                rawUpdateData.Id = rawIdentity;

                using (var metadataReader = new StreamReader(MetadataSource.GetUpdateMetadataStream(update.Identity)))
                {
                    rawUpdateData.XmlUpdateBlob = metadataReader.ReadToEnd();
                }

                returnUpdatesList.Add(rawUpdateData);
            }

            response.updates = returnUpdatesList.ToArray();

            // Deduplicate list of files
            response.fileUrls = returnFilesList.GroupBy(f => f.MUUrl).Select(k => k.First()).ToArray();

            return Task.FromResult(response);
        }

        public Task<GetUpdateDecryptionDataResponse> GetUpdateDecryptionDataAsync(GetUpdateDecryptionDataRequest request)
        {
            throw new NotImplementedException();
        }

        public Task<PingResponse> PingAsync(PingRequest request)
        {
            throw new NotImplementedException();
        }

        public Task<GetDeploymentsResponse> GetDeploymentsAsync(GetDeploymentsRequest request)
        {
            throw new NotImplementedException();
        }

        public Task<GetDriverIdListResponse> GetDriverIdListAsync(GetDriverIdListRequest request)
        {
            throw new NotImplementedException();
        }

        public Task<GetDriverSetDataResponse> GetDriverSetDataAsync(GetDriverSetDataRequest request)
        {
            throw new NotImplementedException();
        }

        public Task<GetRelatedRevisionsForUpdatesResponse> GetRelatedRevisionsForUpdatesAsync(GetRelatedRevisionsForUpdatesRequest request)
        {
            throw new NotImplementedException();
        }

        public Task<DownloadFilesResponse> DownloadFilesAsync(DownloadFilesRequest request)
        {
            throw new NotImplementedException();
        }
    }
}
