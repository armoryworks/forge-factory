extends Node

# Fixed-tick simulation clock, fully decoupled from render/frame rate.
# Renderers/UI must only READ tick_count — never advance it.
const TICK_RATE: float = 20.0
const TICK_INTERVAL: float = 1.0 / TICK_RATE

var tick_count: int = 0
var _accumulator: float = 0.0

func _process(delta: float) -> void:
	_accumulator += delta
	while _accumulator >= TICK_INTERVAL:
		_accumulator -= TICK_INTERVAL
		_advance_tick()

func _advance_tick() -> void:
	tick_count += 1
