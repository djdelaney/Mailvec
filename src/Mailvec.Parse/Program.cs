using Mailvec.Parse;

// The whole host is composed in ParseHost.Build so the tests can start it on
// a random port with a substituted parser; this file only runs it.
ParseHost.Build(args).Run();
