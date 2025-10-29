using AppWebCentralRestaurante.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.ML;
using Microsoft.ML.Data;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace AppWebCentralRestaurante.Services
{
    /// <summary>
    /// Servicio simple de recomendación usando ML.NET (clasificación multiclass).
    /// - Entrenar: Entrena a partir de una colección de ejemplos (ExperienciaId, NumComensales, Restricciones).
    /// - Predict/Recommend: Dado un input devuelve PredictedExperienciaId y los scores por clase.
    /// 
    /// NOTAS:
    /// - Necesita el paquete Microsoft.ML.
    /// - Este ejemplo usa MapValueToKey/MapKeyToValue para multiclass.
    /// - Por simplicidad crea un PredictionEngine por petición (no es ideal para alto throughput).
    /// </summary>
    public class RecommendationService
    {
        private readonly MLContext _ml;
        private readonly string _modelPath;
        private ITransformer? _model;
        private DataViewSchema? _modelSchema;
        private readonly ILogger<RecommendationService> _logger;
        private readonly CentralContext? _db; // opcional, para entrenar desde BD

        public RecommendationService(ILogger<RecommendationService> logger, CentralContext? db = null)
        {
            _ml = new MLContext(seed: 0);
            _logger = logger;
            _db = db;
            _modelPath = Path.Combine(AppContext.BaseDirectory, "models", "recommendation.zip");

            // intentar cargar modelo si existe
            if (File.Exists(_modelPath))
            {
                try
                {
                    using var fs = File.OpenRead(_modelPath);
                    _model = _ml.Model.Load(fs, out _modelSchema);
                    _logger.LogInformation("Modelo de recomendación cargado desde {p}", _modelPath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "No se pudo cargar modelo de recomendación (se puede entrenar).");
                }
            }
        }

        /// <summary>
        /// Entrada para predicción.
        /// </summary>
        public class RecommendationInput
        {
            public float NumComensales { get; set; }
            public string Restricciones { get; set; } = "";
        }

        /// <summary>
        /// Salida de predicción.
        /// PredictedLabel será el string label (mapeado a ExperienciaId como string durante entrenamiento)
        /// Score contiene la probabilidad/score para cada clase (orden según la mapValueToKey internamente).
        /// </summary>
        public class RecommendationOutput
        {
            [ColumnName("PredictedLabel")]
            public string PredictedLabel { get; set; } = "";

            public float[] Score { get; set; } = Array.Empty<float>();
        }

        /// <summary>
        /// Predice la mejor experiencia. Lanza InvalidOperationException si no hay modelo cargado.
        /// Devuelve (experienciaId, scores array, labels array).
        /// </summary>
        public (int PredictedExperienciaId, float[] Scores, string[] Labels) Predict(RecommendationInput input)
        {
            if (_model == null)
                throw new InvalidOperationException("Modelo no cargado. Entrena el modelo primero.");

            // crear engine por petición (PredictionEngine no es thread-safe)
            var predEngine = _ml.Model.CreatePredictionEngine<RecommendationInput, RecommendationOutput>(_model);

            var outp = predEngine.Predict(input);

            // intentamos leer las etiquetas (labels) desde el esquema del modelo si existe
            string[] labels = Array.Empty<string>();
            try
            {
                // obtener columna "Label" del esquema (puede que no exista en algunos modelos/escenarios)
                var labelColumn = _modelSchema.GetColumnOrNull("Label");
                if (labelColumn.HasValue)
                {
                    // VBuffer que recibirá las KeyValues (slot names)
                    var vbuffer = default(VBuffer<ReadOnlyMemory<char>>);

                    // Método de extensión GetKeyValues requiere 'ref' VBuffer<T>
                    // Nota: esto rellena vbuffer con los nombres de las clases/labels
                    labelColumn.Value.GetKeyValues(ref vbuffer);

                    // Convertir ReadOnlyMemory<char> a string[]
                    // DenseValues() retorna un IEnumerable<ReadOnlyMemory<char>> o similar según versión
                    var vals = vbuffer.DenseValues().Select(rm => rm.ToString()).ToArray();
                    if (vals?.Length > 0)
                        labels = vals;
                }
            }
            catch (Exception ex)
            {
                // no crítico: si falla, devolvemos labels vacíos. Loguear si tienes logger.
                _logger?.LogWarning(ex, "No se pudieron obtener las etiquetas (KeyValues) del esquema del modelo.");
            }

            // convert predicted label a int (asumiendo que el label fue la ExperienciaId como string)
            int predictedId = -1;
            if (!string.IsNullOrWhiteSpace(outp?.PredictedLabel) && int.TryParse(outp.PredictedLabel, out var pid))
                predictedId = pid;

            return (predictedId, outp?.Score ?? Array.Empty<float>(), labels);
        }


        /// <summary>
        /// Entrena el modelo a partir de una colección de ejemplos.
        /// Cada ejemplo: (ExperienciaId, NumComensales, Restricciones).
        /// Guarda el modelo en _modelPath.
        /// </summary>
        public void Train(IEnumerable<(int ExperienciaId, int NumComensales, string Restricciones)> examples)
        {
            var samples = examples.Select(e => new
            {
                Label = e.ExperienciaId.ToString(), // label como string (e.g. "1","2","3")
                NumComensales = (float)e.NumComensales,
                Restricciones = e.Restricciones ?? ""
            });

            var ds = _ml.Data.LoadFromEnumerable(samples);

            var pipeline = _ml.Transforms.Conversion.MapValueToKey("Label")
                .Append(_ml.Transforms.Text.FeaturizeText("RestriccionesFeats", "Restricciones"))
                .Append(_ml.Transforms.Concatenate("Features", "NumComensales", "RestriccionesFeats"))
                .Append(_ml.MulticlassClassification.Trainers.SdcaMaximumEntropy(labelColumnName: "Label", featureColumnName: "Features"))
                .Append(_ml.Transforms.Conversion.MapKeyToValue("PredictedLabel"));

            var model = pipeline.Fit(ds);

            // guardar modelo
            Directory.CreateDirectory(Path.GetDirectoryName(_modelPath) ?? ".");
            using var fs = File.Create(_modelPath);
            _ml.Model.Save(model, ds.Schema, fs);

            // actualizar en memoria
            _model = model;
            _modelSchema = ds.Schema;
            _logger.LogInformation("Modelo de recomendación entrenado y guardado en {p}", _modelPath);
        }

        /// <summary>
        /// Entrena el modelo a partir de los logs de recomendación / reservas almacenados en la BD.
        /// Este método requiere que el servicio haya sido construido con una referencia a CentralContext (db no nulo).
        /// </summary>
        public async Task TrainFromDatabaseAsync()
        {
            if (_db == null)
            {
                _logger.LogWarning("No hay contexto DB disponible para entrenar desde la base de datos.");
                return;
            }

            // recolectar ejemplos útiles: preferimos usar Reservas (resultado real) si están disponibles,
            // fallback a RecomendacionesLog si no.
            var reservas = await _db.Reservas
                .AsNoTracking()
                .Where(r => r.ExperienciaId > 0)
                .Select(r => new { r.ExperienciaId, r.NumComensales, r.Restricciones })
                .ToListAsync();

            var examples = new List<(int, int, string)>();

            foreach (var r in reservas)
            {
                examples.Add((r.ExperienciaId, r.NumComensales, r.Restricciones ?? ""));
            }

            // si no hay suficientes datos, intentar con RecomendacionesLog (si guardaste CaracteristicasJson)
            if (examples.Count < 30) // umbral arbitrario
            {
                var logs = await _db.RecomendacionesLog
                    .AsNoTracking()
                    .Where(x => x.ExperienciaId != null && !string.IsNullOrEmpty(x.CaracteristicasJson))
                    .ToListAsync();

                foreach (var lg in logs)
                {
                    try
                    {
                        var doc = JsonDocument.Parse(lg.CaracteristicasJson);
                        // intentar extraer campos numéricos sencillos
                        int num = 1;
                        string restr = "";

                        if (doc.RootElement.TryGetProperty("Draft", out var draftEl))
                        {
                            if (draftEl.TryGetProperty("Personas", out var pEl) && pEl.TryGetInt32(out var pVal)) num = pVal;
                            if (draftEl.TryGetProperty("Restricciones", out var rEl) && rEl.ValueKind == JsonValueKind.String) restr = rEl.GetString() ?? "";
                        }

                        if (lg.ExperienciaId.HasValue)
                        {
                            examples.Add((lg.ExperienciaId.Value, num, restr));
                        }
                    }
                    catch
                    {
                        // skip parse errors
                    }
                }
            }

            if (examples.Count == 0)
            {
                _logger.LogWarning("No hay datos suficientes para entrenar el modelo de recomendación.");
                return;
            }

            Train(examples);
        }
    }
}
