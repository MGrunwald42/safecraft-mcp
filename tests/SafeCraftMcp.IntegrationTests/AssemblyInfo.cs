// The integration tests start real sandboxes, and some measure timeouts against PhotoCraft's work
// (SessionTests' timeout and close-during-call tests). In parallel they compete for the CPU and
// the timings stop meaning anything, so the classes run one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
