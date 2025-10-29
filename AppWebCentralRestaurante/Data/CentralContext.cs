using AppWebCentralRestaurante.Controllers;
using AppWebCentralRestaurante.Models;
using Microsoft.EntityFrameworkCore;
using System;

namespace AppWebCentralRestaurante.Data
{
    public class CentralContext : DbContext
    {
        public CentralContext(DbContextOptions<CentralContext> options) : base(options) { }

        public DbSet<Usuario> Usuarios { get; set; }
        public DbSet<Experiencia> Experiencias { get; set; }
        public DbSet<Reserva> Reservas { get; set; }
        public DbSet<Preferencia> Preferencias { get; set; }
        public DbSet<RecomendacionLog> RecomendacionesLog { get; set; }
        public DbSet<Reporte> Reportes { get; set; }

        public DbSet<EmailVerificacion> EmailVerificaciones { get; set; }


        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Ejemplos de restricciones / mapeos mínimos
            modelBuilder.Entity<Usuario>()
                .HasIndex(u => u.Email)
                .IsUnique();

            // Relaciones uno-a-muchos
            modelBuilder.Entity<Usuario>()
                .HasMany(u => u.Reservas)
                .WithOne(r => r.Usuario)
                .HasForeignKey(r => r.UsuarioId)
                .OnDelete(DeleteBehavior.SetNull);

            modelBuilder.Entity<Experiencia>()
                .HasMany(e => e.Reservas)
                .WithOne(r => r.Experiencia)
                .HasForeignKey(r => r.ExperienciaId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<Usuario>()
                .HasMany(u => u.Preferencias)
                .WithOne(p => p.Usuario)
                .HasForeignKey(p => p.UsuarioId)
                .OnDelete(DeleteBehavior.Cascade);

            // Si quieres, aquí puedes añadir check constraints (Estados) y valores por defecto SQL.
            // Ejemplo (opcional):
            // modelBuilder.Entity<Reserva>()
            //     .HasCheckConstraint("CHK_Reservas_Estado", "Estado IN ('pendiente','confirmada','cancelada','completada')");
        }
    }
}
