// The app's statics (CommandManager, the shell icon cache, the one WPF test dispatcher) are shared by every test.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
