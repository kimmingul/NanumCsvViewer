using System.Globalization;
using System.Text;
using NanumCsvViewer.Stats;

namespace NanumCsvViewer
{
    public partial class Form1
    {
        /// <summary>결과 창의 '모형 저장'. report.Model이 ModelBundle일 때만 버튼이 보인다.</summary>
        private void SaveAdvancedModel(AdvancedReport report)
        {
            if (report.Model is not ModelBundle bundle)
            {
                MessageBox.Show(this,
                    LT("This result has no savable model.", "이 결과에는 저장할 모형이 없습니다."),
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using var dlg = new SaveFileDialog
            {
                Title = LT("Save Model", "모형 저장"),
                Filter = "JSON (*.json)|*.json",
                FileName = SafeModelFile(report.Title) + ".json",
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                ModelStore.Save(bundle, dlg.FileName, AppInfo.Version, report.CreatedAt);
                MessageBox.Show(this,
                    LT($"Saved {bundle.ModelType} ({bundle.TrainingRows:N0} training rows).\n{dlg.FileName}",
                       $"{bundle.ModelType} 모형을 저장했습니다(학습 {bundle.TrainingRows:N0}행).\n{dlg.FileName}"),
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, Stats.ErrorText.Localize(ex.Message), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>결과 창의 ONNX 내보내기. 지원하지 않는 모형은 이유를 알리고 파일을 만들지 않는다.</summary>
        private void ExportOnnx(AdvancedReport report)
        {
            if (report.Model is not ModelBundle bundle)
            {
                MessageBox.Show(this,
                    LT("This result has no savable model.", "이 결과에는 저장할 모형이 없습니다."),
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!OnnxExport.TryExport(bundle, out var package, out var reason) || package is null)
            {
                MessageBox.Show(this, reason ?? LT("This model is not exportable to ONNX.", "이 모형은 ONNX로 내보낼 수 없습니다."),
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var precision = OnnxPrecision.Float32;
            if (OnnxExport.SupportsFloat64(bundle))
            {
                var choice = AskOnnxPrecision();
                if (choice is null) return;
                precision = choice.Value;
                if (precision == OnnxPrecision.Float64
                    && (!OnnxExport.TryExport(bundle, precision, out package, out reason) || package is null))
                {
                    MessageBox.Show(this, reason ?? LT("This model is not exportable to ONNX.", "이 모형은 ONNX로 내보낼 수 없습니다."),
                        Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
            }
            using var dlg = new SaveFileDialog
            {
                Title = LT("Export ONNX", "ONNX 내보내기"),
                Filter = "ONNX (*.onnx)|*.onnx",
                FileName = SafeModelFile(report.Title) + (precision == OnnxPrecision.Float64 ? "-float64" : "") + ".onnx",
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                File.WriteAllBytes(dlg.FileName, package.Model);
                string sidecar = Path.ChangeExtension(dlg.FileName, ".features.json");
                File.WriteAllText(sidecar, package.SidecarJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                MessageBox.Show(this,
                    package.Summary + "\n\n" + dlg.FileName + "\n" + sidecar,
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, Stats.ErrorText.Localize(ex.Message), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>
        /// 밀집 그래프 모형(선형·GLM·LDA·다항 로지스틱·SVM·나이브 베이즈)의 ONNX 정밀도 선택. 기본은 float32,
        /// float64는 입력·상수·출력이 모두 double이다. 취소하면 null.
        /// </summary>
        private OnnxPrecision? AskOnnxPrecision()
        {
            using var dlg = new Form
            {
                Text = LT("Export ONNX", "ONNX 내보내기"),
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(14),
                BackColor = _palette.Window,
                ForeColor = _palette.Text,
            };
            var layout = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill,
            };
            const int textWidth = 460;
            var f32 = new RadioButton
            {
                Name = "OnnxFloat32Radio",
                Text = LT("float32 (default)", "float32 (기본)"),
                Checked = true,
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 0),
            };
            var f32Note = new Label
            {
                Text = LT("Smallest file, works everywhere. A score within about 1e-5 of a decision boundary may be classified differently from the app.",
                    "가장 작고 어디서나 동작합니다. 결정 경계에 1e-5 이내인 점수는 앱과 다르게 분류될 수 있습니다."),
                AutoSize = true,
                MaximumSize = new Size(textWidth - 24, 0),
                Margin = new Padding(24, 0, 0, 10),
            };
            var f64 = new RadioButton
            {
                Name = "OnnxFloat64Radio",
                Text = LT("double precision (float64)", "배정밀도 (float64)"),
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 0),
            };
            var f64Note = new Label
            {
                Text = LT("Input, constants and outputs are double, so labels match the app's double arithmetic except for scores within about 1e-12 of a boundary. The file is about twice as large and the runtime must feed float64 input. Not available for trees, forests or boosting.",
                    "입력·상수·출력이 모두 double이라 결정 경계에 1e-12 이내인 점수를 빼면 앱의 double 산술과 같은 라벨이 나옵니다. 파일이 약 두 배 크고 실행 환경에서 float64 입력을 넣어야 합니다. 트리·포레스트·부스팅에는 쓸 수 없습니다."),
                AutoSize = true,
                MaximumSize = new Size(textWidth - 24, 0),
                Margin = new Padding(24, 0, 0, 12),
            };
            var ok = new Button { Name = "OnnxPrecisionOk", Text = LT("OK", "확인"), DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(88, 28) };
            var cancel = new Button { Name = "OnnxPrecisionCancel", Text = LT("Cancel", "취소"), DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(88, 28) };
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Width = textWidth, WrapContents = false };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            layout.Controls.Add(f32);
            layout.Controls.Add(f32Note);
            layout.Controls.Add(f64);
            layout.Controls.Add(f64Note);
            layout.Controls.Add(buttons);
            dlg.Controls.Add(layout);
            dlg.AcceptButton = ok;
            dlg.CancelButton = cancel;
            if (dlg.ShowDialog(this) != DialogResult.OK) return null;
            return f64.Checked ? OnnxPrecision.Float64 : OnnxPrecision.Float32;
        }

        private async void AdvApplyModel()
        {
            if (_doc is null || _closing || _busy || !_doc.IndexingComplete) return;
            using var open = new OpenFileDialog
            {
                Title = LT("Apply Saved Model", "저장된 모형 적용"),
                Filter = "JSON (*.json)|*.json",
            };
            if (open.ShowDialog(this) != DialogResult.OK) return;
            string modelPath = open.FileName;
            var doc = _doc;
            var stored = await RunAnalysisOperationAsync(doc, (_, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return ModelStore.LoadFile(modelPath);
            });
            if (stored is null || _closing || IsDisposed || !ReferenceEquals(doc, _doc)) return;
            var model = stored.Model;
            var headers = AdvHeaders();
            var binding = ModelStore.Bind(model, headers);
            if (!binding.Ready)
            {
                MessageBox.Show(this,
                    LT("The current view is missing columns this model needs.", "현재 보기에 이 모형이 필요로 하는 열이 없습니다.")
                    + (binding.MissingPredictionColumns.Count > 0
                        ? "\n" + LT(
                            $"This model was fitted with an offset, exposure or trials column, so the new data needs the same numeric column(s): {string.Join(", ", binding.MissingPredictionColumns)}.",
                            $"이 모형은 오프셋·노출·시행 수 열로 적합되어, 새 데이터에도 같은 수치 열이 있어야 합니다: {string.Join(", ", binding.MissingPredictionColumns)}.")
                        : "")
                    + "\n" + (binding.Error ?? ""),
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            using var save = new SaveFileDialog
            {
                Title = LT("Export Predictions", "예측 내보내기"),
                Filter = "CSV (*.csv)|*.csv",
                FileName = SafeModelFile(model.ModelType) + "-predictions.csv",
            };
            if (save.ShowDialog(this) != DialogResult.OK) return;

            int n = doc.DisplayRowCount;
            var sourceRows = new long[n];
            for (int i = 0; i < n; i++) sourceRows[i] = doc.GetSourceRowNumber(i);
            int targetCol = ResolveHeader(headers, model.Target);
            string path = save.FileName;
            var summary = await RunAnalysisOperationAsync(doc, (rows, ct) =>
            {
                if (rows.Count != sourceRows.Length)
                    throw new ModelStoreException("The view changed while the model was being applied. No file was written.");
                return WritePredictions(model, binding, rows, sourceRows, targetCol, path, ct);
            });
            if (summary is null || _closing || IsDisposed) return;
            ShowResult(LT("Apply Saved Model", "저장된 모형 적용"), summary);
        }

        private static string WritePredictions(
            ModelBundle model, ModelBinding binding, IReadOnlyList<string[]> rows, long[] sourceRows,
            int targetCol, string path, CancellationToken cancellation)
        {
            var metrics = new ApplyMetrics(model);
            var names = model.ClassNames;
            bool classify = model.Task == ModelTask.Classification;
            // SVM·AdaBoost 분류는 확률을 내지 않는다 — 빈 확률 열을 만들지 않는다.
            bool withProb = classify && names != null && model.Engine is not (SvmModel or AdaBoostModel);
            bool expectedColumn = model.TrialsColumn != null;
            try
            {
                using var writer = new StreamWriter(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                writer.Write(expectedColumn ? "source_row,prediction,expected_successes" : "source_row,prediction");
                if (withProb)
                    for (int c = 0; c < names!.Count; c++) writer.Write(",prob_" + c.ToString(CultureInfo.InvariantCulture));
                writer.WriteLine(",scorable,reason");
                var batch = new RowPrediction[Math.Min(ModelStore.ApplyBatchSize, Math.Max(rows.Count, 1))];
                for (int offset = 0; offset < rows.Count; offset += ModelStore.ApplyBatchSize)
                {
                    cancellation.ThrowIfCancellationRequested();
                    int count = Math.Min(ModelStore.ApplyBatchSize, rows.Count - offset);
                    if (batch.Length < count) batch = new RowPrediction[count];
                    ModelStore.ScoreMany(model, binding, rows, offset, count, batch, cancellation);
                    for (int i = 0; i < count; i++)
                    {
                        var pred = batch[i];
                        string actual = targetCol >= 0 ? (targetCol < rows[offset + i].Length ? rows[offset + i][targetCol] : "") : "";
                        metrics.Add(pred, targetCol >= 0 ? actual : null, targetCol >= 0);
                        writer.Write(sourceRows[offset + i].ToString(CultureInfo.InvariantCulture));
                        writer.Write(',');
                        writer.Write(CsvCell(pred.Scorable ? (classify ? pred.ClassLabel ?? "" : pred.Value.ToString("G17", CultureInfo.InvariantCulture)) : ""));
                        if (expectedColumn)
                        {
                            writer.Write(',');
                            if (pred.Scorable) writer.Write(pred.ExpectedSuccesses.ToString("G17", CultureInfo.InvariantCulture));
                        }
                        if (withProb)
                        {
                            for (int c = 0; c < names!.Count; c++)
                            {
                                writer.Write(',');
                                if (pred.Scorable && pred.Probability != null && c < pred.Probability.Length)
                                    writer.Write(pred.Probability[c].ToString("G17", CultureInfo.InvariantCulture));
                            }
                        }
                        writer.Write(pred.Scorable ? ",1," : ",0,");
                        writer.WriteLine(CsvCell(pred.Reason ?? ""));
                    }
                }
            }
            catch
            {
                try { File.Delete(path); } catch { /* 부분 파일은 남기지 않는다. */ }
                throw;
            }
            var sb = new StringBuilder();
            sb.AppendLine(model.ModelType + " · " + model.Target);
            sb.AppendLine(path);
            sb.AppendLine(LT(
                "source_row is the 1-based data row number (header excluded), kept under filter and sort. Predictions were not inserted into the grid.",
                "source_row는 헤더를 뺀 1부터의 데이터 행 번호이며 필터·정렬 뒤에도 원래 위치입니다. 예측은 그리드에 넣지 않았습니다."));
            if (withProb)
                sb.AppendLine("prob_0.." + (names!.Count - 1).ToString(CultureInfo.InvariantCulture) + ": " + string.Join(", ", names));
            else if (classify)
                sb.AppendLine(LT("This model type gives class labels only (no probabilities).", "이 모형 종류는 클래스만 내고 확률은 내지 않습니다."));
            if (model.UsesPredictionColumns)
                sb.AppendLine(LT(
                    "Prediction used the new data's " + string.Join(", ", new[] { model.OffsetColumn, model.ExposureColumn, model.TrialsColumn }.Where(c => c != null)) +
                    " column(s): mean = inverse link(Xβ + offset + ln exposure)" + (expectedColumn ? "; prediction is the probability, expected_successes = trials × probability" : "") +
                    ". Rows with a missing, non-numeric or invalid value (exposure ≤ 0, trials not a positive integer) are marked not scorable with the reason.",
                    "예측은 새 데이터의 " + string.Join(", ", new[] { model.OffsetColumn, model.ExposureColumn, model.TrialsColumn }.Where(c => c != null)) +
                    " 열을 사용했습니다: 평균 = 역연결(Xβ + 오프셋 + ln 노출)" + (expectedColumn ? "; prediction은 확률, expected_successes = 시행 수 × 확률" : "") +
                    ". 값이 없거나 수치가 아니거나 올바르지 않은 행(노출 ≤ 0, 시행 수가 양의 정수 아님)은 이유와 함께 채점 불가로 표시했습니다."));
            metrics.Finish();
            sb.Append(metrics.Format());
            return sb.ToString();
        }

        private static int ResolveHeader(IReadOnlyList<string> headers, string name)
        {
            for (int i = 0; i < headers.Count; i++)
                if (string.Equals(headers[i], name, StringComparison.Ordinal)) return i;
            int found = -1;
            for (int i = 0; i < headers.Count; i++)
            {
                if (!string.Equals(headers[i], name, StringComparison.OrdinalIgnoreCase)) continue;
                if (found >= 0) return -1;
                found = i;
            }
            return found;
        }

        private static string CsvCell(string value)
        {
            if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0)
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            return value;
        }

        private static string SafeModelFile(string title)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) title = title.Replace(c, '_');
            title = title.Trim();
            return title.Length == 0 ? "model" : title;
        }

        /// <summary>결과 본문에 붙이는 저장 모형 안내. 지표는 평가 분할, 저장본은 사용 행 전체.</summary>
        private static string AdvSavedNote(int rows)
            => LT(
                $"Saved model fitted on all {rows:N0} used rows. Metrics above are from the evaluation split, not in-sample scores of the saved model.",
                $"저장 모형은 사용한 {rows:N0}행 전체에 적합했습니다. 위 지표는 평가 분할의 것이며 저장 모형의 표본 내 점수가 아닙니다.");
    }
}
