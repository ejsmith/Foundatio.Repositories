using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Foundatio.Repositories.Models;
using Nest;
using Foundatio.Repositories.Elasticsearch.Queries.Builders;
using Foundatio.Repositories.Extensions;
using Foundatio.Utility;

namespace Foundatio.Repositories.Elasticsearch.Extensions {
    public static class ElasticIndexExtensions {
        public static FindResults<T> ToFindResults<T>(this ISearchResponse<T> response, int? limit = null) where T : class, new() {
            var docs = response.Hits.Take(limit ?? Int32.MaxValue).ToFindHits().ToList();
            var data = response.ScrollId != null ? new DataDictionary { { ElasticDataKeys.ScrollId, response.ScrollId } } : null;
            return new FindResults<T>(docs, response.Total, response.ToAggregationResult(), null, data);
        }

        public static IEnumerable<FindHit<T>> ToFindHits<T>(this IEnumerable<IHit<T>> hits) where T : class {
            return hits.Select(h => h.ToFindHit());
        }

        public static FindHit<T> ToFindHit<T>(this IGetResponse<T> hit) where T : class {
            var versionedDoc = hit.Source as IVersioned;
            if (versionedDoc != null && hit.Version != null)
                versionedDoc.Version = Int64.Parse(hit.Version);

            var data = new DataDictionary { { ElasticDataKeys.Index, hit.Index }, { ElasticDataKeys.IndexType, hit.Type } };
            return new FindHit<T>(hit.Id, hit.Source, 0, versionedDoc?.Version ?? null, data);
        }

        public static FindHit<T> ToFindHit<T>(this IHit<T> hit) where T : class {
            var versionedDoc = hit.Source as IVersioned;
            if (versionedDoc != null && hit.Version != null)
                versionedDoc.Version = Int64.Parse(hit.Version);

            var data = new DataDictionary { { ElasticDataKeys.Index, hit.Index }, { ElasticDataKeys.IndexType, hit.Type } };
            return new FindHit<T>(hit.Id, hit.Source, hit.Score, versionedDoc?.Version ?? null, data);
        }

        public static FindHit<T> ToFindHit<T>(this IMultiGetHit<T> hit) where T : class {
            var versionedDoc = hit.Source as IVersioned;
            if (versionedDoc != null && hit.Version != null)
                versionedDoc.Version = Int64.Parse(hit.Version);

            var data = new DataDictionary { { ElasticDataKeys.Index, hit.Index }, { ElasticDataKeys.IndexType, hit.Type } };
            return new FindHit<T>(hit.Id, hit.Source, 0, versionedDoc?.Version ?? null, data);
        }

        public static IDictionary<string, AggregationResult> ToAggregationResult(this IDictionary<string, IAggregation> aggregations) {
            var result = new Dictionary<string, AggregationResult>();
            if (aggregations == null || aggregations.Count == 0)
                return null;

            foreach (var key in aggregations.Keys) {
                var aggValue = aggregations[key];

                var metricAggValue = aggValue as ValueMetric;
                if (metricAggValue != null) {
                    result.Add(key, new AggregationResult { Value = metricAggValue.Value });
                    continue;
                }

                var bucketValue = aggValue as Bucket;
                if (bucketValue != null) {
                    var aggResult = new AggregationResult {
                        Buckets = new List<BucketResult>()
                    };

                    foreach (var keyItem in bucketValue.Items.OfType<KeyItem>()) {
                        var bucketResult = new BucketResult {
                            Key = keyItem.Key,
                            KeyAsString = keyItem.KeyAsString,
                            Total = keyItem.DocCount
                        };

                        bucketResult.Aggregations = keyItem.Aggregations.ToAggregationResult();

                        aggResult.Buckets.Add(bucketResult);
                    }

                    foreach (var keyItem in bucketValue.Items.OfType<HistogramItem>()) {
                        var bucketResult = new BucketResult {
                            Key = keyItem.Key.ToString(),
                            KeyAsString = keyItem.KeyAsString,
                            Total = keyItem.DocCount
                        };

                        bucketResult.Aggregations = keyItem.Aggregations.ToAggregationResult();

                        aggResult.Buckets.Add(bucketResult);
                    }

                    result.Add(key, aggResult);
                    continue;
                }
            }

            return result;
        }

        public static IDictionary<string, AggregationResult> ToAggregationResult<T>(this ISearchResponse<T> res) where T : class {
            return res.Aggregations.ToAggregationResult();
        }

        public static Task<IBulkResponse> IndexManyAsync<T>(this IElasticClient client, IEnumerable<T> objects, Func<T, string> getParent, Func<T, string> getIndex = null, string type = null) where T : class {
            if (objects == null)
                throw new ArgumentNullException(nameof(objects));

            if (getParent == null && getIndex == null)
                return client.IndexManyAsync(objects, null, type);

            var indexBulkRequest = CreateIndexBulkRequest(objects, getIndex, type, getParent);
            return client.BulkAsync(indexBulkRequest);
        }

        private static BulkRequest CreateIndexBulkRequest<T>(IEnumerable<T> objects, Func<T, string> getIndex, string type, Func<T, string> getParent) where T : class {
            var bulkRequest = new BulkRequest();
            TypeNameMarker typeNameMarker = type;
            bulkRequest.Type = typeNameMarker;
            var list = objects.Select(o => {
                var doc = new BulkIndexOperation<T>(o);
                if (getParent != null)
                    doc.Parent = getParent(o);

                if (getIndex != null)
                    doc.Index = getIndex(o);

                var versionedDoc = o as IVersioned;
                if (versionedDoc != null)
                    doc.Version = versionedDoc.Version.ToString();

                return doc;
            }).Cast<IBulkOperation>().ToList();
            bulkRequest.Operations = list;
            
            return bulkRequest;
        }

        public static PropertiesDescriptor<T> SetupDefaults<T>(this PropertiesDescriptor<T> pd) where T : class {
            var hasIdentity = typeof(IIdentity).IsAssignableFrom(typeof(T));
            var hasDates = typeof(IHaveDates).IsAssignableFrom(typeof(T));
            var hasCreatedDate = typeof(IHaveCreatedDate).IsAssignableFrom(typeof(T));
            var supportsSoftDeletes = typeof(ISupportSoftDeletes).IsAssignableFrom(typeof(T));

            if (hasIdentity)
                pd.String(p => p.Name(d => (d as IIdentity).Id).IndexName("id").Index(FieldIndexOption.NotAnalyzed));

            if (supportsSoftDeletes)
                pd.Boolean(p => p.Name(d => (d as ISupportSoftDeletes).IsDeleted).IndexName(SoftDeletesQueryBuilder.Fields.Deleted));

            if (hasCreatedDate)
                pd.Date(p => p.Name(d => (d as IHaveCreatedDate).CreatedUtc).IndexName("created"));

            if (hasDates)
                pd.Date(p => p.Name(d => (d as IHaveDates).UpdatedUtc).IndexName("updated"));

            return pd;
        }
    }
}