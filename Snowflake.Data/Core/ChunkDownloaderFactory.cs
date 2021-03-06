﻿using System.Threading;
using Snowflake.Data.Configuration;

namespace Snowflake.Data.Core
{
    class ChunkDownloaderFactory
    {
        public static IChunkDownloader GetDownloader(QueryExecResponseData responseData,
                                                     SFBaseResultSet resultSet,
                                                     CancellationToken cancellationToken)
        {
            if (SFConfiguration.Instance().UseV2ChunkDownloader)
            {
                return new SFChunkDownloaderV2(responseData.rowType.Count,
                    responseData.chunks,
                    responseData.qrmk,
                    responseData.chunkHeaders,
                    cancellationToken);
            }
            else
            {
                return new SFBlockingChunkDownloader(responseData.rowType.Count,
                    responseData.chunks,
                    responseData.qrmk,
                    responseData.chunkHeaders,
                    cancellationToken,
                    resultSet);
            }
        }
    }
}
