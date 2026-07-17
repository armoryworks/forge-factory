extends Camera2D

# Camera model — isometric-design.md §4, and the A2 checklist in
# docs/iso-review-scaffold.md.
#
# NO ROTATION, deliberately and permanently in v1. Rotation quadruples the art bill
# (every directional machine needs 4 renders per rotation state), doubles the belt sprite
# set, and makes blueprints ambiguous. §4 settles this — if someone asks for it, the
# answer is there, not here.

const ZOOM_STOPS: Array[float] = [0.125, 0.25, 0.5, 1.0, 2.0]
const ZOOM_DEFAULT_INDEX := 3

# Smoothing rate for the glide between stops. Frame-rate independent (see _process) —
# a raw lerp(a, b, RATE * delta) is not, and would zoom differently at 60 vs 144 fps.
const ZOOM_LERP_RATE := 16.0
const ZOOM_EPSILON := 0.0001

# Screen-space pan speed, px/sec. Divided by zoom at use so the factory slides under the
# cursor at a constant *visual* rate whether you are at 0.125 or 2.0 (§4).
const PAN_SPEED := 900.0

var _zoom_index: int = ZOOM_DEFAULT_INDEX
var _target_zoom: float = ZOOM_STOPS[ZOOM_DEFAULT_INDEX]
var _dragging: bool = false

# Zoom-to-cursor state. We pin the WORLD point that was under the mouse when the zoom
# started and re-anchor it every frame of the glide — not just once at the end — or the
# anchor drifts visibly while the animation runs.
var _anchor_world: Vector2 = Vector2.ZERO
var _anchor_active: bool = false

func _ready() -> void:
	_target_zoom = ZOOM_STOPS[_zoom_index]
	zoom = Vector2(_target_zoom, _target_zoom)

func _unhandled_input(event: InputEvent) -> void:
	if event is InputEventMouseButton:
		var mb: InputEventMouseButton = event
		if mb.button_index == MOUSE_BUTTON_WHEEL_UP and mb.pressed:
			_step_zoom(1)
		elif mb.button_index == MOUSE_BUTTON_WHEEL_DOWN and mb.pressed:
			_step_zoom(-1)
		elif mb.button_index == MOUSE_BUTTON_MIDDLE:
			_dragging = mb.pressed
	elif event is InputEventMouseMotion and _dragging:
		# World-space grab: the world point under the cursor stays under the cursor.
		# Dividing by zoom is what makes it a grab rather than a fixed-speed drag.
		var mm: InputEventMouseMotion = event
		position -= mm.relative / zoom.x
		_anchor_active = false

func _step_zoom(direction: int) -> void:
	var next: int = clampi(_zoom_index + direction, 0, ZOOM_STOPS.size() - 1)
	if next == _zoom_index:
		return
	_zoom_index = next
	_target_zoom = ZOOM_STOPS[next]
	_anchor_world = get_global_mouse_position()
	_anchor_active = true

func _process(delta: float) -> void:
	_pan_keys(delta)
	_apply_zoom(delta)

func _pan_keys(delta: float) -> void:
	var dir := Vector2.ZERO
	if Input.is_key_pressed(KEY_W):
		dir.y -= 1.0
	if Input.is_key_pressed(KEY_S):
		dir.y += 1.0
	if Input.is_key_pressed(KEY_A):
		dir.x -= 1.0
	if Input.is_key_pressed(KEY_D):
		dir.x += 1.0
	if dir == Vector2.ZERO:
		return
	# No inertia/momentum (§4): precision beats feel for this audience. Movement stops
	# the frame the key does.
	position += dir.normalized() * PAN_SPEED * delta / zoom.x
	_anchor_active = false

func _apply_zoom(delta: float) -> void:
	var current: float = zoom.x
	if absf(current - _target_zoom) <= ZOOM_EPSILON:
		if _anchor_active:
			zoom = Vector2(_target_zoom, _target_zoom)
			position += _anchor_world - get_global_mouse_position()
			_anchor_active = false
		return
	# Exponential smoothing — frame-rate independent, unlike lerp(a, b, rate * delta).
	var t: float = 1.0 - exp(-ZOOM_LERP_RATE * delta)
	var next_zoom: float = lerpf(current, _target_zoom, t)
	zoom = Vector2(next_zoom, next_zoom)
	if _anchor_active:
		position += _anchor_world - get_global_mouse_position()
