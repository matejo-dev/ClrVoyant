// ClrVoyant server entry point. All wiring lives in HostFactory (testable); this
// just runs the configured transport (stdio by default, or HTTP when
// CLRVOYANT_TRANSPORT=http).
await ClrVoyant.Server.HostFactory.RunAsync(args);
