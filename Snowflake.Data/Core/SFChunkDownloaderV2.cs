﻿/*
 * Copyright (c) 2012-2017 Snowflake Computing Inc. All rights reserved.
 */

using System;
using System.IO.Compression;
using System.IO;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Snowflake.Data.Log;

namespace Snowflake.Data.Core
{
    class SFChunkDownloaderV2 : IChunkDownloader
    {
        static private SFLogger logger = SFLoggerFactory.GetLogger<SFChunkDownloaderV2>();

        private List<SFResultChunk> chunks;

        private string qrmk;
        
        // External cancellation token, used to stop donwload
        private CancellationToken externalCancellationToken;

        //TODO: parameterize prefetch slot
        private const int prefetchSlot = 5;
        
        private static IRestRequest restRequest = RestRequestImpl.Instance;
        
        private Dictionary<string, string> chunkHeaders;

        public SFChunkDownloaderV2(int colCount, List<ExecResponseChunk>chunkInfos, string qrmk, 
            Dictionary<string, string> chunkHeaders, CancellationToken cancellationToken)
        {
            this.qrmk = qrmk;
            this.chunkHeaders = chunkHeaders;
            this.chunks = new List<SFResultChunk>();
            externalCancellationToken = cancellationToken;

            var idx = 0;
            foreach(ExecResponseChunk chunkInfo in chunkInfos)
            {
                this.chunks.Add(new SFResultChunk(chunkInfo.url, chunkInfo.rowCount, colCount, idx++));
            }
            logger.Info($"Total chunk number: {chunks.Count}");

            FillDownloads();
        }

        private BlockingCollection<Lazy<Task<SFResultChunk>>> _downloadTasks;
        private ConcurrentQueue<Lazy<Task<SFResultChunk>>> _downloadQueue;

        private void RunDownloads()
        {
            try
            {
                while (_downloadQueue.TryDequeue(out var task) && !externalCancellationToken.IsCancellationRequested)
                {
                    if (!task.IsValueCreated)
                    {
                        task.Value.Wait(externalCancellationToken);
                    }
                }
            }
            catch (Exception)
            {
                //Don't blow from background threads.
            }
        }


        private void FillDownloads()
        {
            _downloadTasks = new BlockingCollection<Lazy<Task<SFResultChunk>>>();

            foreach (var c in chunks)
            {
                var t = new Lazy<Task<SFResultChunk>>(() => DownloadChunkAsync(new DownloadContextV2()
                {
                    chunk = c,
                    chunkIndex = c.ChunkIndex,
                    qrmk = this.qrmk,
                    chunkHeaders = this.chunkHeaders,
                    cancellationToken = this.externalCancellationToken,
                }));

                _downloadTasks.Add(t);
            }

            _downloadTasks.CompleteAdding();

            _downloadQueue = new ConcurrentQueue<Lazy<Task<SFResultChunk>>>(_downloadTasks);

            for (var i = 0; i < prefetchSlot && i < chunks.Count; i++)
                Task.Run(new Action(RunDownloads));

        }

        public Task<SFResultChunk> GetNextChunkAsync()
        {
            return _downloadTasks.IsCompleted ? Task.FromResult<SFResultChunk>(null) : _downloadTasks.Take().Value;
        }
        
        private async Task<SFResultChunk> DownloadChunkAsync(DownloadContextV2 downloadContext)
        {
            logger.Info($"Start downloading chunk #{downloadContext.chunkIndex+1}");
            SFResultChunk chunk = downloadContext.chunk;

            chunk.downloadState = DownloadState.IN_PROGRESS;

            S3DownloadRequest downloadRequest = new S3DownloadRequest()
            {
                uri = new UriBuilder(chunk.url).Uri,
                qrmk = downloadContext.qrmk,
                // s3 download request timeout to one hour
                timeout = TimeSpan.FromHours(1),
                httpRequestTimeout = TimeSpan.FromSeconds(16),
                chunkHeaders = downloadContext.chunkHeaders
            };

            var httpResponse = await restRequest.GetAsync(downloadRequest, downloadContext.cancellationToken).ConfigureAwait(false);
            Stream stream = await httpResponse.Content.ReadAsStreamAsync().ConfigureAwait(false);

            if (httpResponse.Content.Headers.TryGetValues("Content-Encoding", out var encoding))
            {
                if (string.Equals(encoding.First(), "gzip", StringComparison.OrdinalIgnoreCase))
                {
                    stream = new GZipStream(stream, CompressionMode.Decompress);
                }
            }

            parseStreamIntoChunk(stream, chunk);
            
            chunk.downloadState = DownloadState.SUCCESS;
            logger.Info($"Succeed downloading chunk #{downloadContext.chunkIndex+1}");

            return chunk;
        }

        
        /// <summary>
        ///     Content from s3 in format of 
        ///     ["val1", "val2", null, ...],
        ///     ["val3", "val4", null, ...],
        ///     ...
        ///     To parse it as a json, we need to preappend '[' and append ']' to the stream 
        /// </summary>
        /// <param name="content"></param>
        /// <param name="resultChunk"></param>
        private static void parseStreamIntoChunk(Stream content, SFResultChunk resultChunk)
        {
            Stream openBracket = new MemoryStream(Encoding.UTF8.GetBytes("["));
            Stream closeBracket = new MemoryStream(Encoding.UTF8.GetBytes("]"));

            Stream concatStream = new ConcatenatedStream(new Stream[3] { openBracket, content, closeBracket});

            IChunkParser parser = ChunkParserFactory.GetParser(concatStream);
            parser.ParseChunk(resultChunk);
        }
    }

    class DownloadContextV2
    {
        public SFResultChunk chunk { get; set; }

        public int chunkIndex { get; set; }

        public string qrmk { get; set; }

        public Dictionary<string, string> chunkHeaders { get; set; }

        public CancellationToken cancellationToken { get; set; }
    }
}
