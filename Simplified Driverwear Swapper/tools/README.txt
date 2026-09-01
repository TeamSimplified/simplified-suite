Dev harnesses only — nothing in here ships with the app.

Put verification tools here: the pattern that worked well on the Chassis
Swapper was a harness that runs the real logic against a known-good reference
file and asserts the output matches, so the rules can be proven before any of
them reach the UI.

The app project deliberately does not glob this folder into its output.
