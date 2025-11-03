using AppWebCentralRestaurante.Models;
using Microsoft.EntityFrameworkCore;

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
        public DbSet<Perfile> Perfiles { get; set; }
        public DbSet<Evento> Eventos { get; set; }

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

            modelBuilder.Entity<Perfile>(b =>
            {
                b.HasKey(p => p.PerfilId);
                b.HasIndex(p => p.UsuarioId).IsUnique(false);
                b.HasIndex(p => p.AnonId).IsUnique(false);
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
