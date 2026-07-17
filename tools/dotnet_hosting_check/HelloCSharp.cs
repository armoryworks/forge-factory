using Godot;

// B23 dotnet-hosting-check: proves a C# script builds and runs inside the
// Godot 4.7 .NET-enabled ("mono") build, not just as a standalone console
// benchmark (B7). Hosting check only -- no in-engine integration.
public partial class HelloCSharp : Node
{
    public override void _Ready()
    {
        GD.Print("DOTNET_HOSTING_CHECK result=PASS message=C#_Ready_executed_inside_Godot");
        GetTree().Quit(0);
    }
}
