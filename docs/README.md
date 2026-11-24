# MDL Optimizer user guide

MDL Optimizer reads and writes Studio MDL v10 models for Counter-Strike 1.6 and Half-Life. It simplifies meshes and compares rendered images to check the result. The app runs offline on 64-bit Windows with .NET Framework 4.8. It runs independently of Steam, Blender, and Python.

## Optimize a model

1. Extract the entire ZIP archive and open **`MdlOptimizer.exe`** in the extracted folder.
2. Drag one or more `.mdl` files into the window. For a split model, select the main MDL and keep the companion texture and animation files alongside it.
3. Choose an output folder and click **Optimize**. **Full** checks every animation sequence. **Quick** samples a subset for a faster run.
4. Select a completed file. Drag the divider to compare the original and optimized images in the view with the largest difference. Select **Full body** to see the whole character or **Differences** to highlight changed pixels.
5. Click **Open result folder** to open the folder containing the exported MDL in Windows File Explorer. For a batch, select the completed model you want to inspect. In **Exact copy only** mode, the button opens the exact copy's folder. To install a player skin in CS 1.6, copy its model folder to `cstrike/models/player/`.

On first launch, the app uses the Windows display language. It supports Brazilian Portuguese, English, Spanish, and Russian, and defaults to English for other languages. Use **Language** at the top of the window to switch at any time, including during optimization. The app saves your choice for future runs. Select **System language** to follow Windows again.

## Output files

| File | Contents |
| --- | --- |
| `<name>_exato/<name>_exato.mdl` | The exact copy. Cleanup removes confirmed duplicate opaque faces and faces that always have zero area because coincident vertices share a bone. The control images must match pixel for pixel. |
| `<name>_opt/<name>_opt.mdl` | The candidate with the fewest triangles that passed the search checks for this model. If no further reduction passes, this file uses the exact copy's geometry. |
| `antes.png`, `depois.png`, `diferencas.png` | Before, after, and difference images for the view with the largest discrepancy. The legend identifies silhouette, color, material, and depth changes. The `corpo-*.png` images show the largest discrepancy among the primary full-body views. |
| `relatorio.json` | Triangle counts for the entire file and the default body configuration, SHA-256 hashes, candidates tested, visual metrics, and the animation, frame, and camera angle for the worst view. |
| `auditoria-reservada.png` | In Full mode, a comparison of the optimized model in its worst held-out view. These views are reserved for evaluating the final export. |

**Exact copy only** skips the additional mesh reduction for in-game use. Each run writes to a new folder. If a model fails or you cancel the run, the app discards that model's partial output. Previously completed models remain available.

## How the optimizer chooses a mesh

Exact cleanup compares bone-local vertex positions, normals and their bone assignments, texture coordinates, materials, and face winding. It preserves back faces and ambiguous overlaps. Vertices attached to different bones can move apart during animation, so the optimizer treats them separately.

For the in-game model, the optimizer tries edge collapses that end at existing mesh corners. It considers surface deformation, UV displacement, and changes in normals. It protects boundaries, seams, joints, and sensitive layers. Eight geometry profiles establish a quality baseline. Four give extra protection to extremities.

Version 1.1 adds four profiles that vary the amount of reduction according to surface visibility across several poses. The visibility map influences the cost of each collapse. A face hidden in a sampled pose still belongs to the mesh. In uniformly colored regions, the optimizer can simplify UV mapping if the covered texels retain the same palette index in every skin family. Materials with special effects receive extra protection.

The optimizer can combine patches from different candidates when they have compatible topology and share the same boundary, including its winding, vertices, normals, and UVs. If a combination fails, the optimizer can undo collapses near the visual difference, restore dependent geometry, and test the remaining patches. Full mode can also split a group that failed and test which parts can be simplified independently.

The rasterizer uses the same pose, camera, and scale for both images. It accounts for face winding, depth, textures, cutout transparency, additive materials, and fullbright materials. It measures silhouette overlap, RGB error, and changes in the visible surface across the image and within smaller regions. A small hole in a character's face can fail a candidate even when the full-body average is within limits.

Full validation samples two views per sequence and includes representative full-body poses, close-ups, bodygroup alternatives, and skin families. A second pass examines the body and details at other angles, frames, and animation blends. The optimizer **writes and reopens the MDL** before making its final decision. The model reader checks exported faces, vertices, normals, and UVs. It also verifies that bones, animations, events, hitboxes, attachments, textures, and palettes are preserved. For supported split models, companion texture files ending in `T.mdl` and animation groups ending in `01.mdl`, `02.mdl`, and so on are consolidated into one standalone output MDL.

When refining the initial geometry baseline, the optimizer allows mean RGB error to rise by at most 0.75 on the 0–255 scale and silhouette intersection over union (IoU) to fall by at most 0.25 percentage points on the primary pass. The candidate must also meet the absolute limits on both passes. The report records the refinement limits in `game.refinement_budget`, the primary pass in `game.visual`, and the additional views in `game.detail`.

After selecting the in-game model, Full mode checks it with held-out cameras, frames, and blends. The result appears in the window and in `game.reserved`. A failed audit flags a difference the search missed. Open `auditoria-reservada.png` to inspect the worst case. For an export that matches the original pixels across all three sets of views, use Full mode and take the MDL from the `_exato` folder. In Quick mode, `reserved` is null.

The report measures silhouette IoU on a 0–1 scale and mean RGB error on a 0–255 scale. Screening requires an overall IoU of at least 0.989, mean RGB error of at most 3.3, and material changes in at most 1.4% of the compared area. Limits also apply to individual views and regions. The tests use orthographic projection, nearest-neighbor texture sampling, and diagnostic lighting. They sample selected frames and blends within each sequence. Map lighting, entity effects, real-time interpolation, and frame rate depend on the GoldSrc client.

## Command line

Open PowerShell in the folder containing the executables. Replace the example input paths with paths to your models. The verification commands use the output path from the first optimization run.

```powershell
& '.\MdlOptimizer.Cli.exe' info '.\models\player.mdl'
& '.\MdlOptimizer.Cli.exe' optimize '.\models\player.mdl' '.\models\creature.mdl' --out '.\output'
& '.\MdlOptimizer.Cli.exe' optimize '.\models\prop.mdl' --out '.\output' --exact-only --fast
& '.\MdlOptimizer.Cli.exe' verify '.\models\player.mdl' '.\output\player\player_opt\player_opt.mdl' --preview '.\comparison.png'
& '.\MdlOptimizer.Cli.exe' verify '.\models\player.mdl' '.\output\player\player_opt\player_opt.mdl' --strict
```

The CLI writes JSON results to standard output and progress to standard error. `verify` compares models derived from the same model structure. Use `verify --strict` to require the held-out views to pass as well.

| Exit code | Meaning |
| --- | --- |
| `0` | Success |
| `1` | One or more models in a batch failed |
| `2` | Input or execution error |
| `3` | Visual validation failed in `verify` |
| `130` | Run canceled |

The CLI uses the app's language preference. To choose a language for one run, pass `--lang` before the command. For example, `.\MdlOptimizer.Cli.exe --lang en --help` displays help in English. Supported values are `system`, `pt-BR`, `en`, `es`, and `ru`.
