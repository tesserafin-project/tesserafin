#pragma warning disable CS1591

using System;
using System.IO;
using Microsoft.Extensions.Logging;
using Tesserafin.Common.Configuration;
using Tesserafin.Controller.LiveTv;

namespace Tesserafin.LiveTv.Timers
{
    public class SeriesTimerManager : ItemDataProvider<SeriesTimerInfo>
    {
        public SeriesTimerManager(ILogger<SeriesTimerManager> logger, IConfigurationManager config)
            : base(
                logger,
                Path.Combine(config.CommonApplicationPaths.DataPath, "livetv/seriestimers.json"),
                (r1, r2) => string.Equals(r1.Id, r2.Id, StringComparison.OrdinalIgnoreCase))
        {
        }

        /// <inheritdoc />
        public override void Add(SeriesTimerInfo item)
        {
            ArgumentException.ThrowIfNullOrEmpty(item.Id);

            base.Add(item);
        }
    }
}
