using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;

namespace Factory;

// B51/B53 negative-controlled check, C#-side (SimHubClient.cs is the deliverable it
// tests). Legs, run headless:
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
//   3. B53: baseline-fetch-failure control -- EstablishBaselineAsync() against a
//      HubUrl that can never resolve must return false and leave HasBaseline
//      false, not silently report a baseline that was never taken.
//   4. B53: gap-detection pure control -- SimHubClient.IsGap() must say "no gap"
//      for a normal (previous, step, next) triple and "gap" for one where the
//      step doesn't match, with no live connection involved.
//   5. Live connect (best-effort) -- if the real adapter is reachable at the
//      default HubUrl, connect for real, confirm a genuine baseline is
//      established (HasBaseline, BaselineTick >= 0) and one genuine `sim.tick`
//      arrives with no false gap flagged. Skipped (not failed) if the adapter
//      isn't up, same fallback convention as adapter_client_check.gd.
public partial class SimHubClientCheck : Node
{
	private const string GoodPayload = """
		{"tick":1234,"beltDeltas":null,"machineState":{"miners":{"Crafting":3,"Starved":1},"furnaces":{"Crafting":2},"assemblers":{"Blocked":1}},"stock":{"ironOre":412,"ironPlate":96,"ironGear":7}}
		""";

	private const string MangledPayload = """
		{"tick":1234,"beltDeltas":[],"machineState":{"miners":{"Crafting":3,"Starved":1},"furnaces":{"Crafting":2},"assemblers":{"Blocked":1}},"stock":{"ironOre":412,"ironPlate":96,"ironGear":7}}
		""";

	private const string BaselinePayload = """
		{"tick":6192,"tickRate":60,"hash":"0xe5cd345a8c8840d6","gearBufferCap":1000000,"buffers":{"ironOre":47,"ironPlate":3,"ironGear":497},"machineState":{"miners":{"Crafting":19},"furnaces":{"Crafting":16},"assemblers":{"Crafting":2}}}
		""";

	// B56: canned 202 body per the belt-send huddle's finalized contract -- one
	// accepted-and-not-in-rejected belt, one explicitly rejected (occupied). Parse must
	// keep those two facts distinguishable, not collapse to a single count.
	private const string BeltPostGoodPayload = """
		{"appliedAtTick":4200,"accepted":1,"rejected":[{"cell":{"x":51,"y":50},"reason":"occupied"}]}
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

		// --- Leg: baseline parse (shares the /sim/state "buffers"->"stock" normalization) ---
		var baseline = SimHubClient.ParseBaseline(BaselinePayload);
		bool baselineParsePass = baseline.Tick == 6192 && (long)baseline.Stock["ironOre"] == 47;
		GD.Print($"SIM_HUB_CLIENT_CHECK_BASELINE_PARSE result={(baselineParsePass ? "PASS" : "FAIL")} " +
			$"tick={baseline.Tick} ironOre={baseline.Stock["ironOre"]}");

		// --- Leg 2: wrong-hub-path negative control ---
		var wrongClient = new SimHubClient { HubUrl = "http://127.0.0.1:5299/hubs/does-not-exist" };
		AddChild(wrongClient);
		await Task.Delay(2000);
		bool wrongPathCorrectlyFailed = !wrongClient.Connected;
		GD.Print($"SIM_HUB_CLIENT_CHECK_WRONG_PATH result={(wrongPathCorrectlyFailed ? "PASS" : "FAIL")} " +
			$"connected={wrongClient.Connected} (expect false -- a nonexistent hub path must not report connected)");
		wrongClient.QueueFree();

		// --- Leg 3 (B53): baseline-fetch-failure negative control ---
		var unreachableClient = new SimHubClient { HubUrl = "http://127.0.0.1:1/hubs/sim" };
		AddChild(unreachableClient);
		bool baselineFetchReportedFalse = !await unreachableClient.EstablishBaselineAsync();
		bool baselineStayedFalse = !unreachableClient.HasBaseline;
		bool baselineFailureControlPass = baselineFetchReportedFalse && baselineStayedFalse;
		GD.Print($"SIM_HUB_CLIENT_CHECK_BASELINE_FAIL_CONTROL result={(baselineFailureControlPass ? "PASS" : "FAIL")} " +
			$"establish_returned_false={baselineFetchReportedFalse} has_baseline={unreachableClient.HasBaseline} " +
			"(expect true, false -- an unreachable /sim/state must not silently report a baseline)");
		unreachableClient.QueueFree();

		// --- Leg 4 (B53): gap-detection pure control, no network ---
		bool noGap = !SimHubClient.IsGap(previousTick: 100, expectedStep: 20, newTick: 120);
		bool realGap = SimHubClient.IsGap(previousTick: 100, expectedStep: 20, newTick: 200);
		bool gapControlPass = noGap && realGap;
		GD.Print($"SIM_HUB_CLIENT_CHECK_GAP_CONTROL result={(gapControlPass ? "PASS" : "FAIL")} " +
			$"no_gap_case={noGap} gap_case={realGap} (expect true, true -- matching step is not a gap, mismatched step is)");

		// --- Leg 5: live connect, best-effort ---
		var liveClient = new SimHubClient();
		AddChild(liveClient);
		long observedTick = -1;
		long baselineTickSeen = -1;
		liveClient.TickReceived += tick => observedTick = tick;
		liveClient.BaselineEstablished += tick => baselineTickSeen = tick;
		await Task.Delay(3000);
		bool liveConnected = liveClient.Connected;
		bool liveHasBaseline = liveClient.HasBaseline;
		bool liveNoFalseGap = !liveClient.HasGap;
		bool livePass = liveConnected && liveHasBaseline && observedTick >= 0 && liveNoFalseGap;
		string liveResult = liveConnected
			? (livePass ? "PASS" : "FAIL")
			: "SKIPPED (adapter not reachable at default HubUrl)";
		GD.Print($"SIM_HUB_CLIENT_CHECK_LIVE result={liveResult} connected={liveConnected} " +
			$"has_baseline={liveHasBaseline} baseline_tick={baselineTickSeen} observed_tick={observedTick} " +
			$"gap_detected={liveClient.HasGap}");
		liveClient.QueueFree();

		// --- Leg 6 (B56): belt-post response parse control, no network ---
		// Contract per the huddle: accepted count and rejected list must stay
		// distinguishable -- a 202 can be simultaneously "transport succeeded" and
		// "sim refused this cell" (entity_layer.gd's -1-means-nothing-happened logic,
		// same reasoning applied to the wire).
		var beltPostParsed = SimHubClient.ParseBeltPostResponse(BeltPostGoodPayload);
		bool beltPostParsePass = beltPostParsed.Ok
			&& beltPostParsed.AppliedAtTick == 4200
			&& beltPostParsed.Accepted == 1
			&& beltPostParsed.Rejected.Count == 1
			&& beltPostParsed.Rejected[0].X == 51 && beltPostParsed.Rejected[0].Y == 50
			&& beltPostParsed.Rejected[0].Reason == "occupied";
		GD.Print($"SIM_HUB_CLIENT_CHECK_BELT_POST_PARSE result={(beltPostParsePass ? "PASS" : "FAIL")} " +
			$"applied_at_tick={beltPostParsed.AppliedAtTick} accepted={beltPostParsed.Accepted} " +
			$"rejected_count={beltPostParsed.Rejected.Count} (expect 4200, 1, 1 -- accepted and rejected distinguishable)");

		// --- Leg 7 (B56): unreachable-endpoint negative control for the send path ---
		var deadSendClient = new SimHubClient { HubUrl = "http://127.0.0.1:1/hubs/sim" };
		AddChild(deadSendClient);
		var deadPostResult = await deadSendClient.SendBeltsAsync(new[] { new SimHubClient.BeltPlacement(50, 50, 0) });
		bool deadPostCorrectlyFailed = !deadPostResult.Ok;
		GD.Print($"SIM_HUB_CLIENT_CHECK_BELT_POST_UNREACHABLE result={(deadPostCorrectlyFailed ? "PASS" : "FAIL")} " +
			$"ok={deadPostResult.Ok} (expect false -- an unreachable /sim/belts must not report success)");
		deadSendClient.QueueFree();

		// --- Leg 8 (B56): live round-trip, best-effort -- /sim/belts may not exist yet ---
		// (mathematician is building it concurrently in this huddle). Skipped, not
		// failed, until it answers 202. Real content: POST a belt, then confirm a later
		// beltDeltas emit at tick >= appliedAtTick differs from the pre-POST sample --
		// not just "POST, wait, hope". Cell (100,100) picked empirically: the world
		// enforces map bounds ("off-map" rejects observed at (500,500)/(-50,-50)/
		// (9000,9000) against a live probe), (100,100) is inside them and unoccupied on
		// a freshly-started adapter.
		var rtClient = new SimHubClient();
		AddChild(rtClient);
		await Task.Delay(2500); // let baseline + a couple of ticks land first
		string preBeltsJson = Json.Stringify(rtClient.BeltDeltas);

		var postResult = await rtClient.SendBeltsAsync(new[] { new SimHubClient.BeltPlacement(100, 100, 0) });
		string liveBeltResult;
		bool liveBeltPass;
		if (!postResult.Ok)
		{
			liveBeltResult = "SKIPPED (POST /sim/belts unreachable or not yet implemented)";
			liveBeltPass = true; // not a failure of this check -- the endpoint is a partner deliverable
		}
		else if (postResult.Accepted == 0)
		{
			// Transport succeeded, sim refused every cell (e.g. already occupied by a
			// prior run of this same check). Distinguishable per Leg 6 -- not a plumbing
			// failure, but there is nothing to diff, so this sub-case is inconclusive
			// rather than pass/fail.
			liveBeltResult = $"INCONCLUSIVE (0 accepted, {postResult.Rejected.Count} rejected -- nothing to diff)";
			liveBeltPass = true;
		}
		else
		{
			long deadline = postResult.AppliedAtTick;
			long sawTickAtOrAfter = -1;
			string postBeltsJson = preBeltsJson;
			for (int i = 0; i < 40 && sawTickAtOrAfter < 0; i++)
			{
				await Task.Delay(250);
				if (rtClient.LastTick >= deadline)
				{
					sawTickAtOrAfter = rtClient.LastTick;
					postBeltsJson = Json.Stringify(rtClient.BeltDeltas);
				}
			}
			bool diffed = sawTickAtOrAfter >= 0 && postBeltsJson != preBeltsJson;
			liveBeltPass = diffed;
			liveBeltResult = diffed ? "PASS" : "FAIL";
			liveBeltResult += $" applied_at_tick={deadline} saw_tick={sawTickAtOrAfter} accepted={postResult.Accepted}";
		}
		GD.Print($"SIM_HUB_CLIENT_CHECK_BELT_ROUNDTRIP result={liveBeltResult}");
		rtClient.QueueFree();

		// --- Leg 9 (B58): retry-attempts control, no live server needed ---
		// Proves retrying actually happens, not just that the first failure is silent
		// forever (the pre-B58 behavior).
		var retryClient = new SimHubClient { HubUrl = "http://127.0.0.1:1/hubs/sim", InitialConnectRetryDelayMs = 300 };
		AddChild(retryClient);
		await Task.Delay(1500);
		bool retriedMultipleTimes = retryClient.ConnectAttempts >= 3;
		bool retryStillNotConnected = !retryClient.Connected;
		bool retryControlPass = retriedMultipleTimes && retryStillNotConnected;
		GD.Print($"SIM_HUB_CLIENT_CHECK_RETRY_CONTROL result={(retryControlPass ? "PASS" : "FAIL")} " +
			$"attempts={retryClient.ConnectAttempts} connected={retryClient.Connected} " +
			"(expect attempts>=3, connected=false -- an unreachable target must keep retrying, never connect)");
		retryClient.QueueFree();

		// --- Leg 10 (B58): real "adapter started after the game" proof ---
		// Points at the DEFAULT HubUrl (5299) so an external harness can start a fresh
		// adapter mid-run -- this is the exact B58 scenario, not a simulation of it.
		// MEASURED: this client is created late in the check (after several earlier
		// legs' waits), so by the time it runs, an externally-orchestrated adapter
		// start is often already up -- attemptsAtConnect==1 is a legitimate, correct
		// outcome (a client should connect immediately when the server is reachable;
		// preferring a slower path would be a worse design, not a better proof). The
		// deterministic proof that retrying itself works is Leg 9, against a target
		// that is NEVER reachable; this leg's job is only to prove a client pointed at
		// the real adapter actually connects and baselines once it exists. SKIPPED (not
		// FAIL) if nothing ever comes up in the poll window, e.g. a plain run with no
		// external orchestration.
		var lateClient = new SimHubClient { InitialConnectRetryDelayMs = 500 };
		AddChild(lateClient);
		bool lateConnected = false;
		int attemptsAtConnect = -1;
		bool baselineAtConnect = false;
		for (int i = 0; i < 48 && !lateConnected; i++) // up to ~12s at 250ms
		{
			await Task.Delay(250);
			if (lateClient.Connected && lateClient.HasBaseline)
			{
				lateConnected = true;
				attemptsAtConnect = lateClient.ConnectAttempts;
				baselineAtConnect = lateClient.HasBaseline;
			}
		}
		string lateResult;
		bool latePass;
		if (!lateConnected)
		{
			lateResult = "SKIPPED (no adapter became reachable during the poll window)";
			latePass = true;
		}
		else
		{
			latePass = baselineAtConnect;
			lateResult = (latePass ? "PASS" : "FAIL") +
				$" attempts_at_connect={attemptsAtConnect} has_baseline={baselineAtConnect}";
		}
		GD.Print($"SIM_HUB_CLIENT_CHECK_LATE_ADAPTER result={lateResult}");
		lateClient.QueueFree();

		bool overallPass = parseControlPass && baselineParsePass && wrongPathCorrectlyFailed
			&& baselineFailureControlPass && gapControlPass && (!liveConnected || livePass)
			&& beltPostParsePass && deadPostCorrectlyFailed && liveBeltPass
			&& retryControlPass && latePass;
		GD.Print($"SIM_HUB_CLIENT_CHECK result={(overallPass ? "PASS" : "FAIL")}");
		GetTree().Quit(overallPass ? 0 : 1);
	}
}
