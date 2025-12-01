using AppWebCentralRestaurante.Models;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Linq;
using System;
using System.Threading.Tasks;

namespace AppWebCentralRestaurante.Data
{
    public class ApplicationDbContext : DbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> opts) : base(opts) { }

        public DbSet<Usuario> Usuarios { get; set; }
        public DbSet<Experiencia> Experiencias { get; set; }
        public DbSet<Reserva> Reservas { get; set; }
        public DbSet<Preferencia> Preferencias { get; set; }
        public DbSet<RecomendacionLog> RecomendacionesLog { get; set; }
        public DbSet<AnonSession> AnonSessions { get; set; }
        public DbSet<Evento> Eventos { get; set; }

        public async Task<List<ReservaViewModel>> GetReservasDelDia()
        {
            //var hoy = DateTime.Today;
            //var mañana = hoy.AddDays(1);

            return await Reservas
                //.Where(r => r.FechaHora >= hoy && r.FechaHora < mañana)
                .Select(r => new ReservaViewModel
                {
                    Id = r.Id,
                    Hora = r.FechaHora.TimeOfDay,
                    CodigoReserva = "RES-" + r.Id.ToString("D3"),
                    NumeroPersonas = r.NumComensales,
                    Alergias = r.Restricciones,
                    Estado = r.Estado,
                    NombreCliente = r.Usuario != null ? r.Usuario.Nombre : r.NombreReserva,
                    NombreExperiencia = r.Experiencia.Nombre,
                    DescripcionExperiencia = r.Experiencia.Descripcion,
                    PrecioExperiencia = r.Experiencia.Precio
                })
                .ToListAsync();
        }


        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<AnonSession>(b =>
            {
                b.HasKey(a => a.AnonId);
                b.Property(a => a.AnonId).ValueGeneratedOnAdd();
                b.Property(a => a.Estado).HasMaxLength(50).HasDefaultValue("activo");
            });

            modelBuilder.Entity<Evento>(b =>
            {
                b.HasKey(e => e.EventoId);
                b.Property(e => e.EventoId).ValueGeneratedOnAdd();
                b.Property(e => e.EventType).IsRequired().HasMaxLength(150);
                b.HasIndex(e => e.UsuarioId);
                b.HasIndex(e => e.AnonId);
            });

            modelBuilder.Entity<Preferencia>(b =>
            {
                b.HasKey(p => p.Id);
                b.HasIndex(p => p.UsuarioId);
            });

            modelBuilder.Entity<Reserva>(b =>
            {
                b.Property(r => r.Restricciones).IsRequired(false).HasMaxLength(500);
                b.Property(r => r.Telefono).IsRequired(false).HasMaxLength(50);
                b.Property(r => r.DNI).IsRequired(false).HasMaxLength(50);
            });

            // Configura FK opcionales si quieres:
            // modelBuilder.Entity<Evento>().HasOne(e => e.Usuario).WithMany().HasForeignKey(e => e.UsuarioId).OnDelete(DeleteBehavior.SetNull);
        }
    }
}
