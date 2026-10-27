# SimConnect.dll

`SimConnect.dll` is Microsoft's SimConnect client library (x64), from
Microsoft Flight Simulator. It is Microsoft's software, redistributed
with OGS-TS Bridge so the `msfs` source works out of the box, and is not
covered by this repository's license.

The build copies it next to both executables. The `msfs` source loads it
from there first; `--set simconnect=<path>` points it at another copy.
