﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Sparrow
{
    public class IoMetrics
    {
        public enum MeterType
        {
            WriteUsingSyscall,
            WriteUsingMem,
            Sync
        }

        private readonly ConcurrentDictionary<string, FileIoMetrics> _fileMetrics =
            new ConcurrentDictionary<string, FileIoMetrics>();

        private readonly ConcurrentQueue<string> _closedFiles = new ConcurrentQueue<string>();

        public IoMetrics(int currentBufferSize, int summaryBufferSize)
        {
            BufferSize = currentBufferSize;
            SummaryBufferSize = summaryBufferSize;
        }

        public int BufferSize { get; }
        public int SummaryBufferSize { get; }

        public IEnumerable<FileIoMetrics> Files => _fileMetrics.Values;

        public void FileClosed(string filename)
        {
            FileIoMetrics value;
            if (!_fileMetrics.TryGetValue(filename, out value))
                return;
            value.Closed = true;
            _closedFiles.Enqueue(filename);
            while (_closedFiles.Count > 16)
            {
                if (_closedFiles.TryDequeue(out filename) == false)
                    return;
                _fileMetrics.TryRemove(filename, out value);
            }
        }

        public IoMeterBuffer.DurationMeasurement MeterIoRate(string filename, MeterType type, long size)
        {
            var fileIoMetrics = _fileMetrics.GetOrAdd(filename,
                name => new FileIoMetrics(name, BufferSize, SummaryBufferSize));
            IoMeterBuffer buffer;
            switch (type)
            {
                case MeterType.WriteUsingMem:
                    buffer = fileIoMetrics.WriteUsingMem;
                    break;
                case MeterType.WriteUsingSyscall:
                    buffer = fileIoMetrics.WriteUsingSyscall;
                    break;
                case MeterType.Sync:
                    buffer = fileIoMetrics.Sync;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(type), type, null);
            }
            return new IoMeterBuffer.DurationMeasurement(buffer, type, size);
        }

        public class FileIoMetrics
        {
            public string FileName;
            public IoMeterBuffer Sync;
            public IoMeterBuffer WriteUsingMem;
            public IoMeterBuffer WriteUsingSyscall;
            public bool Closed;

            public FileIoMetrics(string filename, int metricsBufferSize, int summaryBufferSize)
            {
                FileName = filename;

                WriteUsingMem = new IoMeterBuffer(metricsBufferSize, summaryBufferSize);
                WriteUsingSyscall = new IoMeterBuffer(metricsBufferSize, summaryBufferSize);
                Sync = new IoMeterBuffer(metricsBufferSize, summaryBufferSize);
            }


            public List<IoMeterBuffer.MeterItem> GetRecentMetrics()
            {
                var list = new List<IoMeterBuffer.MeterItem>();
                list.AddRange(Sync.GetCurrentItems());
                list.AddRange(WriteUsingMem.GetCurrentItems());
                list.AddRange(WriteUsingSyscall.GetCurrentItems());

                list.Sort((x, y) => x.Start.CompareTo(y.Start));

                return list;
            }

            public List<IoMeterBuffer.SummerizedItem> GetSummaryMetrics()
            {
                var list = new List<IoMeterBuffer.SummerizedItem>();
                list.AddRange(Sync.GetSummerizedItems());
                list.AddRange(WriteUsingSyscall.GetSummerizedItems());
                list.AddRange(WriteUsingMem.GetSummerizedItems());

                list.Sort((x, y) => x.TotalTimeStart.CompareTo(y.TotalTimeStart));

                return list;
            }
        }
    }
}