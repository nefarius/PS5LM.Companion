// Program.Main assigns the process-wide Serilog bootstrap logger, so hosts cannot start in parallel.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
