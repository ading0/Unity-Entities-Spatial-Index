## Unity-Entities Spatial Indexing

Spatial hashing library for Unity Entities (1.0+).
Compatible with burst and generic, which can't be achieved by using Entities in a vanilla manner.
Written for Unity 2022.3 LTS, but can probably be adapted easily to later versions since the Entities API shouldn't change too much post-1.0.


#### Usage

Include source files in Unity with the Entities package downloaded.

#### Features

`SpatialIndex<T>` is the main class of interest; `T` can be any blittable type. Usage is similar to collections in the Unity collections package, and `SpatialIndex<T>` has been written to mimic the API.
Space is assumed to be 3D (as is the case with most of Unity), but it should be straightforward to reduce to 2D.
Works with Entities' collection safety checks.






