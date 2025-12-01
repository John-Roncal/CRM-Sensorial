CREATE DATABASE DBCRMSensorialCentral
GO

USE DBCRMSensorialCentral
-- Tablas mínimas (nombres y campos en español) para el sistema en SQL Server
-- 1) Usuarios (Cliente, Mozo, Chef, Admin)
CREATE TABLE dbo.Usuarios (
    Id INT IDENTITY(1,1) PRIMARY KEY,
    Nombre NVARCHAR(150) NOT NULL,
    Email NVARCHAR(256) NOT NULL UNIQUE,
    PasswordHash NVARCHAR(400) NULL,
    Rol NVARCHAR(20) NOT NULL,            -- 'Cliente','Mozo','Chef','Admin'
    EmailConfirmado BIT NOT NULL DEFAULT 0,
    CreadoEn DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    ActualizadoEn DATETIME2 NULL,
	FirebaseUid NVARCHAR(200) NULL
);

ALTER TABLE dbo.Usuarios
    ADD CONSTRAINT CHK_Usuarios_Rol CHECK (Rol IN ('Cliente','Mozo','Chef','Admin'));


-- 2) Experiencias (las 3 ofertas de Central)
CREATE TABLE dbo.Experiencias (
    Id INT IDENTITY(1,1) PRIMARY KEY,
    Codigo NVARCHAR(10) NOT NULL,          -- ej. '01','02','03'
    Nombre NVARCHAR(250) NOT NULL,         -- ej. 'MENÚ DEGUSTACIÓN'
    DuracionMinutos INT NULL,              -- duración aproximada en minutos
    Descripcion NVARCHAR(1000) NULL,
	Precio DECIMAL(10,2) NULL,
    Activa BIT NOT NULL DEFAULT 1,
    CreadoEn DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);


-- 3) Reservas
CREATE TABLE dbo.Reservas (
    Id INT IDENTITY(1,1) PRIMARY KEY,
    UsuarioId INT NULL,                    -- NULL para reservas de invitados
    NombreReserva NVARCHAR(150) NOT NULL,  -- nombre de quien reserva
    NumComensales INT NOT NULL DEFAULT 1,
    ExperienciaId INT NOT NULL,            -- FK a Experiencias (el cliente elige 1 de las 3)
    Restricciones NVARCHAR(500) NULL,      -- alergias/intolerancias/observaciones
    FechaHora DATETIME2 NOT NULL,          -- fecha y hora de la reserva
    Estado NVARCHAR(30) NOT NULL DEFAULT 'pendiente', -- 'pendiente','confirmada','cancelada','completada'
    CreadoEn DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    ActualizadoEn DATETIME2 NULL,
    CONSTRAINT FK_Reservas_Usuario FOREIGN KEY (UsuarioId) REFERENCES dbo.Usuarios(Id) ON DELETE SET NULL,
    CONSTRAINT FK_Reservas_Experiencia FOREIGN KEY (ExperienciaId) REFERENCES dbo.Experiencias(Id) ON DELETE NO ACTION
);

ALTER TABLE dbo.Reservas
    ADD CONSTRAINT CHK_Reservas_Estado CHECK (Estado IN ('pendiente','confirmada','cancelada','completada'));


-- 4) Preferencias (guardadas por el cliente)
CREATE TABLE dbo.Preferencias (
    Id INT IDENTITY(1,1) PRIMARY KEY,
    UsuarioId INT NOT NULL,
    DatosJson NVARCHAR(MAX) NULL,          -- estructura flexible (likes, dislikes, alergias, etc.)
    CreadoEn DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    ActualizadoEn DATETIME2 NULL,
    CONSTRAINT FK_Preferencias_Usuario FOREIGN KEY (UsuarioId) REFERENCES dbo.Usuarios(Id) ON DELETE CASCADE
);


-- 5) RecomendacionesLog (registro de salidas del chatbot / ML)
CREATE TABLE dbo.RecomendacionesLog (
    Id INT IDENTITY(1,1) PRIMARY KEY,
    UsuarioId INT NULL,
    ReservaId INT NULL,
    ExperienciaId INT NULL,                -- si la recomendación apuntó a una experiencia concreta
    Score FLOAT NULL,                      -- score devuelto por el modelo (si aplica)
    CaracteristicasJson NVARCHAR(MAX) NULL,-- contexto usado para la predicción
    CreadoEn DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT FK_RecLog_Usuario FOREIGN KEY (UsuarioId) REFERENCES dbo.Usuarios(Id) ON DELETE SET NULL,
    CONSTRAINT FK_RecLog_Reserva FOREIGN KEY (ReservaId) REFERENCES dbo.Reservas(Id) ON DELETE SET NULL,
    CONSTRAINT FK_RecLog_Experiencia FOREIGN KEY (ExperienciaId) REFERENCES dbo.Experiencias(Id) ON DELETE SET NULL
);


-- 7) Reportes (registro básico de reportes generados por admin)
CREATE TABLE dbo.Reportes (
    Id INT IDENTITY(1,1) PRIMARY KEY,
    AdminId INT NOT NULL,
    TipoReporte NVARCHAR(150) NOT NULL,    -- ej. 'reservas_por_dia'
    ParametrosJson NVARCHAR(MAX) NULL,
    RutaArchivo NVARCHAR(500) NULL,
    CreadoEn DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT FK_Reportes_Admin FOREIGN KEY (AdminId) REFERENCES dbo.Usuarios(Id) ON DELETE NO ACTION
);

INSERT INTO dbo.Experiencias (Codigo, Nombre, DuracionMinutos, Descripcion, Precio)
VALUES
  ('E1', 'MENÚ DEGUSTACIÓN',                180, 'Una degustación de 12 momentos y 32 preparaciones que recorre el diverso y cambiante Perú: costa, Andes y Amazonía. Cada paso explora un ecosistema distinto, conectando ingredientes, temporadas y territorios.', 1630.00),
  ('E2', 'Inmersión Central + MENÚ DEGUSTACIÓN', 360, 'Acceso exclusivo a los procesos creativos de Central a través de un recorrido por los espacios que lo conforman, culminanando con el menú degustación de 12 momentos (32 preparaciones) y maridaje.', 3653.00),
  ('E3', 'THEOBROMAS LAB',                  120, 'Desde el fruto hasta la barra, esta experiencia revela el proceso detrás de los chocolates que creamos para Central, Kjolle y Mil.En nuestro laboratorio, guiados por el equipo que investiga y produce in situ, exploramos variedades nativas de Theobromas como copoazú, macambo y cacao silvestre. Analizamos, experimentamos, y al final, degustamos.', 1360.00);

  select * from Usuarios
  select * from Preferencias
  select * from Reservas