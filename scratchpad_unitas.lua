function angle(yaw, pitch)
  local sensitivity = 14

  local magic_value = 200 / sensitivity
  mouse.move_rel(yaw * magic_value, pitch * magic_value)
end

function _change_angle_over_frames(yaw, pitch, count)
  local step_yaw = yaw / count
  local step_pitch = pitch / count
  for i = 1, count do
   angle(step_yaw, step_pitch)
   movie.frame_advance()
  end
end

function change_angle_over_frames(yaw, pitch, count)
  concurrent.register_once(_change_angle_over_frames, false, yaw, pitch, count)
end

function equipWeapon(weapon)
    weaponType = weapon
    key.hold(weaponType)
    movie.frame_advance()
    key.release(weaponType)
end

function equipVariant(variant)
    weaponVariant = variant
    key.hold(weaponVariant)
    movie.frame_advance()
    key.release(weaponVariant)
end

function awesomeSauce(amount)
    awesomeAmount = amount
    for i = 1, awesomeAmount do
        key.hold("left ctrl")
        movie.frame_advance()
        key.release("left ctrl")
        movie.frame_advance()
    end
end

env.fps=200

print ("[SETUP] Playback Speed")
movie.playback_speed(3)

print("[SETUP] Intro Skip")
movie.frame_advance(6)
key.hold("escape")
movie.frame_advance()
key.release("escape")
movie.frame_advance()

print("[SETUP] Mouse Movement")
movie.frame_advance(60)
mouse.move(440,630)
mouse.left(true)
movie.frame_advance(5)
mouse.left(false)

print("[LEVEL] Selecting VIOLENT")
movie.frame_advance(150)
mouse.move(320,460)
movie.frame_advance(5)
mouse.left(true)
movie.frame_advance(5)
mouse.left(false)
movie.frame_advance(100)

print("[LEVEL] Selecting ACT 1")
mouse.move(960,680)
movie.frame_advance(5)
mouse.left(true)
movie.frame_advance(5)
mouse.left(false)
movie.frame_advance(40)

print("[LEVEL] Selecting 1-4: Clair De Lune")
mouse.move(1595,660)
mouse.left(true)
movie.frame_advance(5)
mouse.left(false)
movie.playback_speed(1)
movie.frame_advance(60)

print("[LEVEL] Tab Menu")
for i = 1, 2 do
key.hold("right shift")
movie.frame_advance()
key.release("right shift")
movie.frame_advance()
end

print("[WEAPON] Equip Freezeframe")
movie.frame_advance(250)
--movie.playback_speed(0.25)
equipWeapon("Alpha5")
equipVariant("R")
print("[MOVEMENT] Dash to door")
key.hold("W")
movie.frame_advance()
key.hold("left shift")
movie.frame_advance()
key.release("left shift")
movie.frame_advance(30)
key.hold("left shift")
movie.frame_advance()
key.release("left shift")
movie.frame_advance(128)
key.release("W")

print("[WEAPON] Shoot first rocket")
angle(0, -16)
movie.frame_advance()
mouse.left(true)
movie.frame_advance()
mouse.left(false)
movie.frame_advance(26)
mouse.right(true)
movie.frame_advance()
mouse.right(false)
movie.frame_advance(148)

env.fps = 140

print("[MOVEMENT] Slide to door")
key.hold("left ctrl")
movie.frame_advance(42)

print("[WEAPON] Shoot second rocket")
mouse.left(true)
movie.frame_advance()
mouse.left(false)

print("[WEAPON] Equip Core Eject Hammer")
equipWeapon("Alpha2")
movie.frame_advance()
mouse.left(true)
movie.frame_advance(41)
key.release("left ctrl")
movie.frame_advance()
angle(180, 36)
movie.frame_advance(64)

print("[WEAPON] Hit rocket")
mouse.left(false)
movie.frame_advance(50)

print("[WEAPON] Equip SRS Cannon")
equipWeapon("Alpha5")
angle(0, -30.15)
movie.frame_advance()
mouse.right(true)
movie.frame_advance(25)

print("[WEAPON] Throw cannonball and Knuckleblaster punch")
key.hold("C")
mouse.right(false)
movie.frame_advance()
key.release("C")
movie.frame_advance(10)

env.fps = 200

print("[WEAPON] Equip Sawed-On Shotgun")
angle(180, 30)
equipWeapon("Alpha2")
equipVariant("T")
movie.frame_advance(132)

print("[WEAPON] Duckboost")
mouse.left(true)
movie.frame_advance()
mouse.left(false)

print("[WEAPON] Equip Firestarter")
equipWeapon("Alpha5")
equipVariant("T")
angle(0, -36)
movie.frame_advance()

print("[WEAPON] Place oil")
mouse.right(true)
movie.frame_advance(104)

print("[MOVEMENT] Jump")
key.hold("space")
movie.frame_advance()
key.release("space")
movie.frame_advance(12)
angle(0, -73.85)
mouse.right(false)

print("[WEAPON] Equip Core Eject Hammer")
equipWeapon("Alpha2")
movie.frame_advance(126)
