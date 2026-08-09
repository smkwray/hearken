# The single governing definition of the squelch/silence contract.
#
# Both ends must agree on every value here. They previously lived as parallel hand-written
# constants in mac/hear-capture.swift and windows/lib/play.cs, which can silently disagree --
# and the disagreement is asymmetric and unsafe in one direction: a receiver that confirms
# silence sooner than the sender suppresses will forgive a real transport stall as if it were
# source silence. Nothing may hand-write these values again; scripts/gen-squelch-profile.py
# generates both language bindings and scripts/hygiene_check.sh fails if they drift.
#
# Raw v1 carries no greeting, header or version, so a receiver CANNOT detect a mismatched peer
# at runtime. The generated profile hash is logged at connection start on each machine as
# deployment evidence. Real negotiation belongs to protocol v2.

profile_id=pcm48-stereo-s16-squelch-v1
sample_rate=48000
channels=2
frame_bytes=4
silence_peak=16
confirm_frames=12000
heartbeat_ms=2000
