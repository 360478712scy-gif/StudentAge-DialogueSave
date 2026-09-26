# Settings help strip

Tool: built-in `image_gen.imagegen`.

Saved project asset: [settings-help.png](settings-help.png). The existing importer removes the solid magenta matte once and preserves a transparent runtime sprite. The left badge and right rounded cap retain their aspect ratio while only the blank text span stretches.

Design prompt: One long, low, matte white paper tooltip with modest rounded corners, a blue/navy campus stationery badge on the left, a white information icon and a small outlined paper plane. The long right area remains empty for runtime Chinese explanations. No perspective, glow or glossy effects.

Final cleanup prompt:

Keep the blue-and-white information bar and left blue stationery badge exactly unchanged, including crisp silhouette. Replace EVERYTHING outside their silhouette (all grey checkerboard, all paper wrinkles outside, any shadow) with a single perfectly uniform flat vivid chroma-magenta #FF00FF color. No gradients, absolutely no shadow, no noise or texture outside the bar. Background must be RGB 255,0,255 edge to edge. Do NOT attempt a transparent background and do not draw checkerboard. The bar body stays white and the badge stays blue. This is a clean chroma-key production sprite on solid pure magenta. Same position and size.
