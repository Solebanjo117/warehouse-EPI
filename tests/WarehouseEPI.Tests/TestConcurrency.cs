// PostgreSQL integration tests create and drop isolated databases. Bound the
// number of concurrent collections so coverage runs do not saturate the server.
[assembly: CollectionBehavior(MaxParallelThreads = 4)]
