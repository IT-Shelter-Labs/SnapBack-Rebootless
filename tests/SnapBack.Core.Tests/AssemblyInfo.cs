// Some tests switch the process-wide UI language (Loc.Culture); run tests sequentially so that
// language-dependent assertions never race.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
