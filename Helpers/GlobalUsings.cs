// Every MessageBox in the app is the themed one (Views/ThemedMessageBox.cs): Windows' own box always draws light.
// An alias wins over the type that "using System.Windows;" brings in, so no call had to change.
global using MessageBox = SwewolfFileExplorer.Views.ThemedMessageBox;
