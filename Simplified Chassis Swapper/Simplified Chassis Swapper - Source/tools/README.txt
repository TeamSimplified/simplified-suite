Dev harnesses only — nothing in here ships with the app.

SwapVerify runs a swap and asserts the output against a hand-made reference
ERP (resource sets, fragment counts, and every PKG payload as JSON). It is the
acceptance gate for the swap engine:

  dotnet run --project tools/SwapVerify -- <chassis.erp> <team.erp> <old> <new> <reference.erp> [gameDir]

The app project deliberately does not glob this folder into its output.
