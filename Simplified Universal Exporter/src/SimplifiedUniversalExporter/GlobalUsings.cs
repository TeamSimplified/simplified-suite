// The engine moved into the Core; everything in the app reads it.
global using SimplifiedUniversalExporter.Core;

// What implicit usings would bring, for the files linked in from Shared, which
// are written against them. Implicit usings themselves stay off: the ported
// code spells its own, and some of it pulls in WPF namespaces whose names
// collide with these only when every file sees both.
global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Threading;
global using System.Threading.Tasks;
