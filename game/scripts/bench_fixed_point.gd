extends Node

# Rough GDScript-side throughput of Q16.16 fixed-point add/mul, for the
# GDScript-vs-C#-vs-GDExtension sizing call (inventory B7). Not a rigorous
# benchmark — a same-shape counterpart to bench/csharp's console benchmark.
const SCALE := 65536 # 2^16
const OPS := 4_000_000

func _ready() -> void:
	var a: int = 3 * SCALE
	var b: int = 2 * SCALE
	var acc: int = 0

	var start_usec := Time.get_ticks_usec()
	for i in range(OPS):
		acc = acc + a # fixed-point add is plain int add
		var product: int = (a * b) >> 16 # fixed-point mul
		acc = (acc + product) & 0xFFFFFFFF # keep bounded, prevent overflow drift
		a = (a + 1) & 0xFFFFF
	var elapsed_usec := Time.get_ticks_usec() - start_usec

	var elapsed_sec := elapsed_usec / 1_000_000.0
	var ops_per_sec := OPS / elapsed_sec
	print("BENCH_GDSCRIPT ops=%d elapsed_sec=%.4f ops_per_sec=%.0f acc=%d" % [OPS, elapsed_sec, ops_per_sec, acc])
	get_tree().quit(0)
