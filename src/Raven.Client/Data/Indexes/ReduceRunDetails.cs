﻿namespace Raven.Client.Data.Indexes
{
    public class ReduceRunDetails : IndexingPerformanceOperation.IDetails
    {
        public int NumberOfModifiedLeafs { get; set; }

        public int NumberOfModifiedBranches { get; set; }
    }

    public class MapRunDetails : IndexingPerformanceOperation.IDetails
    {
        public string BatchCompleteReason { get; set; }

        public long ProcessPrivateMemory { get; set; }

        public long ProcessWorkingSet { get; set; }

        public long CurrentlyAllocated { get; set; }

        public long AllocationBudget { get; set; }

        public void ToJson(BlittableJsonTextWriter writer, JsonOperationContext context)
        {
            if (CurrentlyAllocated != 0)
            {
                writer.WritePropertyName(nameof(CurrentlyAllocated));
                writer.WriteInteger(CurrentlyAllocated);
                writer.WriteComma();
            }

            if (BatchCompleteReason != null)
            {
                writer.WritePropertyName(nameof(BatchCompleteReason));
                writer.WriteString(BatchCompleteReason);
                writer.WriteComma();
            }

            if (ProcessPrivateMemory == 0)
                return;

            writer.WritePropertyName(nameof(ProcessPrivateMemory));
            writer.WriteInteger(ProcessPrivateMemory);
            writer.WriteComma();

            writer.WritePropertyName(nameof(ProcessWorkingSet));
            writer.WriteInteger(ProcessWorkingSet);
            writer.WriteComma();

            
            writer.WritePropertyName(nameof(AllocationBudget));
            writer.WriteInteger(AllocationBudget);
            writer.WriteComma();
        }
    }
}