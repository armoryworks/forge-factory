using System;
using System.Threading.Tasks;
using Godot;

namespace Factory;

// B48 negative-controlled check, C#-side (SimHubClient.cs is the deliverable it
// tests). Three legs, run headless:
//
//   1. Parse control -- a GOOD payload (matches contract §3.1 exactly, beltDeltas:
//      null) must parse to BeltsModelled=false. A MANGLED payload (beltDeltas: [],
//      the one-character wire difference the contract says is a different fact)
//      must parse to BeltsModelled=true. If both payloads produced the same
//      result, the null-vs-[] distinction would not actually be implemented --
//      this is the standing convention's negative control, not a decoration.
//   2. Wrong-hub-path control -- connecting to a path that does not exist must
//      NOT report Connected=true. Proves the check can register failure at all,
//      not just default-pass.
//   3. Live connect (best-effort) -- if the real adapter is reachable at the
//      default HubUrl, connect for real and wait for one genuine `sim.tick`.
//      Skipped (not failed) if the adapter isn't up, same fallback convention as
//      adapter_client_check.gd.
public partial class SimHubClientCheck : Node
{
	private const string GoodPayload = """
		{"tick":1234,"beltDeltas":null,"machineState":{"miners":{"Crafting":3,"Starved":1},"furnaces":{"Crafting":2},"assemblers":{"Blocked":1}},"stock":{"ironOre":412,"ironPlate":96,"ironGear":7}}
		""";

	private const string MangledPayload = """
		{"tick":1234,"beltDeltas":[],"machineState":{"miners":{"Crafting":3,"Starved":1},"furnaces":{"Crafting":2},"assemblers":{"Blocked":1}},"stock":{"ironOre":412,"ironPlate":96,"ironGear":7}}
		""";

	public override void _Ready()
	{
		_ = RunAsync();
	}

	private async Task RunAsync()
	{
		// --- Leg 1: parse control ---
		var good = SimHubClient.ParseTick(GoodPayload);
		var mangled = SimHubClient.ParseTick(MangledPayload);
		bool parseControlPass = good.Tick == 1234
			&& !good.BeltsModelled
			&& mangled.BeltsModelled
			&& (long)good.Stock["ironOre"] == 412
			&& (long)((Godot.Collections.Dictionary)good.MachineState["miners"])["Crafting"] == 3;

		GD.Print($"SIM_HUB_CLIENT_CHECK_PARSE result={(parseControlPass ? "PASS" : "FAIL")} " +
			$"good_belts_modelled={good.BeltsModelled} mangled_belts_modelled={mangled.BeltsModelled} " +
			$"(expect false then true -- null vs [] must parse differently)");

		// --- Leg 2: wrong-hub-path negative control ---
		var wrongClient = new SimHubClient { HubUrl = "http://127.0.0.1:5299/hubs/does-not-exist" };
		AddChild(wrongClient);
		await Task.Delay(2000);
		bool wrongPathCorrectlyFailed = !wrongClient.Connected;
		GD.Print($"SIM_HUB_CLIENT_CHECK_WRONG_PATH result={(wrongPathCorrectlyFailed ? "PASS" : "FAIL")} " +
			$"connected={wrongClient.Connected} (expect false -- a nonexistent hub path must not report connected)");
		wrongClient.QueueFree();

		// --- Leg 3: live connect, best-effort ---
		var liveClient = new SimHubClient();
		AddChild(liveClient);
		long observedTick = -1;
		liveClient.TickReceived += tick => observedTick = tick;
		await Task.Delay(3000);
		bool liveConnected = liveClient.Connected;
		bool livePass = liveConnected && observedTick >= 0;
		string liveResult = liveConnected
			? (livePass ? "PASS" : "FAIL")
			: "SKIPPED (adapter not reachable at default HubUrl)";
		GD.Print($"SIM_HUB_CLIENT_CHECK_LIVE result={liveResult} connected={liveConnected} observed_tick={observedTick}");
		liveClient.QueueFree();

		bool overallPass = parseControlPass && wrongPathCorrectlyFailed && (!liveConnected || livePass);
		GD.Print($"SIM_HUB_CLIENT_CHECK result={(overallPass ? "PASS" : "FAIL")}");
		GetTree().Quit(overallPass ? 0 : 1);
	}
}
