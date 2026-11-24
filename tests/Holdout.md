# Independent camera audit

After selecting the geometry, Full mode evaluates the exported MDL using a separate set of cameras, frames, and animation blends. The search and repair stages use their own views. The `verify --strict` command includes the held-out audit in its pass/fail decision. The `--fast` option skips this audit.

## Run the audit

The script replays the `independent-camera-fold-v1` view set for one or more candidates and records results for each view. As a control, it compares the original model against itself and requires an exact pixel match. Run the command from the repository root after building the app and setting `$candidate` with the [command-line example](../README.md#command-line-example).

```powershell
.\tests\Run-Holdout.ps1 -Original '.\tests\fixtures\piramide.mdl' -Candidates @($candidate) -OutputDirectory '.\artifacts\audit'
```

Choose a new or empty output directory. Exit code `0` means all candidates passed, `3` means a candidate failed visual validation, and `1` means the audit could not run.

## Read the results

`holdout.json` identifies files by SHA-256 and records the overall metrics. Each `*-views.json` file lists the camera, pose, skin, bodygroups, and metrics for every view of one candidate. The corresponding PNG compares the original and the candidate in the view with the largest discrepancy.

In the right-hand difference map, red marks silhouette changes, blue marks depth changes above the audit threshold, purple marks material changes, and green marks changes in both material and depth. At other pixels, orange shows the RGB difference amplified eightfold. The material is the texture selected by the active skin family. If two meshes share a texture, a different surface can become visible while the color and material still match. Blue can reveal the resulting depth shift.

## Pass criteria

| Per-view metric | Full body | Close-up |
| --- | ---: | ---: |
| Minimum silhouette intersection over union (IoU) | 96.6% | 97.8% |
| Mean absolute error per RGB channel, on a 0–255 scale | ≤ 6.0 | ≤ 5.3 |
| Pixels with a material change | ≤ 4.4% | ≤ 3.4% |
| Pixels differing by more than 32 in any channel | ≤ 8.5% | ≤ 8.0% |
| Pixels with a depth discontinuity | ≤ 6.5% | ≤ 4.5% |
| Material changes within a local block | ≤ 27% | ≤ 20% |
| Pixels differing by more than 75 in any channel within a local block | ≤ 32% | ≤ 22% |
| Depth discontinuities within a local block | ≤ 32% | ≤ 25% |

The percentages use the area covered by either model. The depth test compares pixels covered by both. A depth difference counts as a discontinuity when its projected size exceeds the greater of 3 pixels or 5.5% of the image's longest dimension. Local blocks are 9 to 28 pixels on a side, depending on image size, and require at least 38% coverage. The `worst_ratio_to_limit` field gives the largest ratio of a measured error to its limit. A value above 1 fails the view.

Every view must pass. Across all views, silhouette IoU must be at least 98.9%, mean RGB error at most 3.3, and material changes at most 1.4%. The audit sums pixel counts across views before calculating these overall ratios. All measurements come from sampled offline renders.
