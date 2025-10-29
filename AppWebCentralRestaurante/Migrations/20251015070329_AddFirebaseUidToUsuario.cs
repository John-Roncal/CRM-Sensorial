using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AppWebCentralRestaurante.Migrations
{
    /// <inheritdoc />
    public partial class AddFirebaseUidToUsuario : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EmailVerificaciones",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UsuarioId = table.Column<int>(type: "int", nullable: false),
                    Token = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ExpiraEn = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Usado = table.Column<bool>(type: "bit", nullable: false),
                    CreadoEn = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailVerificaciones", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Experiencias",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Codigo = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Nombre = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    DuracionMinutos = table.Column<int>(type: "int", nullable: true),
                    Descripcion = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Activa = table.Column<bool>(type: "bit", nullable: false),
                    CreadoEn = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Experiencias", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Usuarios",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Nombre = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    PasswordHash = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Rol = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    EmailConfirmado = table.Column<bool>(type: "bit", nullable: false),
                    FirebaseUid = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CreadoEn = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ActualizadoEn = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Usuarios", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Preferencias",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UsuarioId = table.Column<int>(type: "int", nullable: false),
                    DatosJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreadoEn = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ActualizadoEn = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Preferencias", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Preferencias_Usuarios_UsuarioId",
                        column: x => x.UsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Reportes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AdminId = table.Column<int>(type: "int", nullable: false),
                    TipoReporte = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    ParametrosJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RutaArchivo = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreadoEn = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Reportes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Reportes_Usuarios_AdminId",
                        column: x => x.AdminId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Reservas",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UsuarioId = table.Column<int>(type: "int", nullable: true),
                    NombreReserva = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    NumComensales = table.Column<int>(type: "int", nullable: false),
                    ExperienciaId = table.Column<int>(type: "int", nullable: false),
                    Restricciones = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FechaHora = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Estado = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    CreadoEn = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ActualizadoEn = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Reservas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Reservas_Experiencias_ExperienciaId",
                        column: x => x.ExperienciaId,
                        principalTable: "Experiencias",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Reservas_Usuarios_UsuarioId",
                        column: x => x.UsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "RecomendacionesLog",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UsuarioId = table.Column<int>(type: "int", nullable: true),
                    ReservaId = table.Column<int>(type: "int", nullable: true),
                    ExperienciaId = table.Column<int>(type: "int", nullable: true),
                    Score = table.Column<double>(type: "float", nullable: true),
                    CaracteristicasJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreadoEn = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecomendacionesLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecomendacionesLog_Experiencias_ExperienciaId",
                        column: x => x.ExperienciaId,
                        principalTable: "Experiencias",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_RecomendacionesLog_Reservas_ReservaId",
                        column: x => x.ReservaId,
                        principalTable: "Reservas",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_RecomendacionesLog_Usuarios_UsuarioId",
                        column: x => x.UsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Preferencias_UsuarioId",
                table: "Preferencias",
                column: "UsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_RecomendacionesLog_ExperienciaId",
                table: "RecomendacionesLog",
                column: "ExperienciaId");

            migrationBuilder.CreateIndex(
                name: "IX_RecomendacionesLog_ReservaId",
                table: "RecomendacionesLog",
                column: "ReservaId");

            migrationBuilder.CreateIndex(
                name: "IX_RecomendacionesLog_UsuarioId",
                table: "RecomendacionesLog",
                column: "UsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_Reportes_AdminId",
                table: "Reportes",
                column: "AdminId");

            migrationBuilder.CreateIndex(
                name: "IX_Reservas_ExperienciaId",
                table: "Reservas",
                column: "ExperienciaId");

            migrationBuilder.CreateIndex(
                name: "IX_Reservas_UsuarioId",
                table: "Reservas",
                column: "UsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_Usuarios_Email",
                table: "Usuarios",
                column: "Email",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmailVerificaciones");

            migrationBuilder.DropTable(
                name: "Preferencias");

            migrationBuilder.DropTable(
                name: "RecomendacionesLog");

            migrationBuilder.DropTable(
                name: "Reportes");

            migrationBuilder.DropTable(
                name: "Reservas");

            migrationBuilder.DropTable(
                name: "Experiencias");

            migrationBuilder.DropTable(
                name: "Usuarios");
        }
    }
}
