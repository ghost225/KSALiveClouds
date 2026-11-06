# KSA cubemap test vectors

Generated 2026-10-26 by `LiveCloudsCli vectors` (CubeBuilder.DirToFace/FaceDir). Each row was checked against KSA's own `KSA.Celestial.DirectionToCubemap`, called by reflection from KSA.dll 2026.10.19.5638: face exact, uv within 1e-6. Planet-fixed (CCF) directions: Z = north pole, lat = asin(z), lon = atan2(y, x) (Celestial.GetLatitudeFromCcf / GetLongitudeFromCcf). Faces 0-5 = +X, -X, +Y, -Y, +Z, -Z. uv in [0, 1], v = 0 is image row 0. On ties the larger-|component| test goes X, then Y, then Z (`>=`).

| direction (x, y, z), unnormalised ok | face | u | v | note |
|---|---|---|---|---|
| (1, 0, 0) | 0 +X | 0.5 | 0.5 | lat 0 lon 0 (+X centre) |
| (-1, 0, 0) | 1 -X | 0.5 | 0.5 | lat 0 lon 180 (-X centre) |
| (0, 1, 0) | 2 +Y | 0.5 | 0.5 | lat 0 lon 90 (+Y centre) |
| (0, -1, 0) | 3 -Y | 0.5 | 0.5 | lat 0 lon -90 (-Y centre) |
| (0, 0, 1) | 4 +Z | 0.5 | 0.5 | north pole (+Z centre) |
| (0, 0, -1) | 5 -Z | 0.5 | 0.5 | south pole (-Z centre) |
| (1, 1, 0) | 0 +X | 0.5 | 0 | lat 0 lon 45: X/Y edge, tie -> +X |
| (1, 0, 1) | 0 +X | 0 | 0.5 | lat 45 lon 0: X/Z edge, tie -> +X |
| (0, 1, 1) | 2 +Y | 0.5 | 1 | lat 45 lon 90: Y/Z edge, tie -> +Y |
| (-1, 0, -1) | 1 -X | 0 | 0.5 | lat -45 lon 180: -X/-Z edge, tie -> -X |
| (1, 1, 1) | 0 +X | 0 | 0 | cube corner (1,1,1), tie -> +X |
| (0.143326, -0.866138, 0.478814) | 3 -Y | 0.582739 | 0.223592 | lat 28.608 lon -80.604 (LC-39A) |
| (-0.173648, -0.984808, 0) | 3 -Y | 0.411837 | 0.5 | lat 0 lon -100 |
| (0.433013, 0.25, 0.866025) | 4 +Z | 0.75 | 0.355662 | lat 60 lon 30 |
| (-0.241845, 0.241845, -0.939693) | 5 -Z | 0.628683 | 0.371317 | lat -70 lon 135 |

Inverse (face, s = 2u-1, t = 2v-1) -> direction before normalising: +X (1,-t,-s), -X (-1,-t,s), +Y (s,1,t), -Y (s,-1,-t), +Z (s,-t,1), -Z (-s,-t,-1). All round trips above checked to 1e-9.
