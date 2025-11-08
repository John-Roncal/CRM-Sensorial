--------------------------------------------------------------------------------
-- Migración: Añadir soporte de Chatbot / AnonSessions / Perfiles / Eventos
-- Archivo: migracion_chatbot_reservas.sql
-- Ejecutar en la base DBCRMSensorialCentral
--------------------------------------------------------------------------------

-- Asegurar que usamos la base correcta
IF DB_ID('DBCRMSensorialCentral') IS NULL
BEGIN
    CREATE DATABASE DBCRMSensorialCentral;
END
GO

USE DBCRMSensorialCentral;
GO

-- =======================
-- 0) Tabla Usuarios (si no existe, crear la versión base que compartiste)
-- =======================
IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID(N'dbo.Usuarios') AND type = N'U')
BEGIN
    CREATE TABLE dbo.Usuarios (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        Nombre NVARCHAR(150) NOT NULL,
        Email NVARCHAR(256) NOT NULL UNIQUE,
        PasswordHash NVARCHAR(400) NULL,
        Rol NVARCHAR(20) NOT NULL,
        EmailConfirmado BIT NOT NULL DEFAULT 0,
        CreadoEn DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        ActualizadoEn DATETIME2 NULL
    );
    ALTER TABLE dbo.Usuarios ADD CONSTRAINT CHK_Usuarios_Rol CHECK (Rol IN ('Cliente','Mozo','Chef','Admin'));
END

-- agregar FirebaseUid si no existe
IF COL_LENGTH('dbo.Usuarios','FirebaseUid') IS NULL
BEGIN
    ALTER TABLE dbo.Usuarios ADD FirebaseUid NVARCHAR(200) NULL;
END

-- =======================
-- 1) Experiencias (si no existe, crear)
-- =======================
IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID(N'dbo.Experiencias') AND type = N'U')
BEGIN
    CREATE TABLE dbo.Experiencias (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        Codigo NVARCHAR(10) NOT NULL,
        Nombre NVARCHAR(250) NOT NULL,
        DuracionMinutos INT NULL,
        Descripcion NVARCHAR(1000) NULL,
        Activa BIT NOT NULL DEFAULT 1,
        CreadoEn DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
    );
END

-- agregar Precio si no existe
IF COL_LENGTH('dbo.Experiencias','Precio') IS NULL
BEGIN
    ALTER TABLE dbo.Experiencias ADD Precio DECIMAL(10,2) NULL;
END

-- =======================
-- 2) Reservas (si no existe, crear la base)
-- =======================
IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID(N'dbo.Reservas') AND type = N'U')
BEGIN
    CREATE TABLE dbo.Reservas (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        UsuarioId INT NULL,
        NombreReserva NVARCHAR(150) NOT NULL,
        NumComensales INT NOT NULL DEFAULT 1,
        ExperienciaId INT NOT NULL,
        Restricciones NVARCHAR(500) NULL,
        FechaHora DATETIME2 NOT NULL,
        Estado NVARCHAR(30) NOT NULL DEFAULT 'pendiente',
        CreadoEn DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        ActualizadoEn DATETIME2 NULL
    );
    ALTER TABLE dbo.Reservas
        ADD CONSTRAINT CHK_Reservas_Estado CHECK (Estado IN ('pendiente','confirmada','cancelada','completada'));
    ALTER TABLE dbo.Reservas
        ADD CONSTRAINT FK_Reservas_Experiencia FOREIGN KEY (ExperienciaId) REFERENCES dbo.Experiencias(Id) ON DELETE NO ACTION;
END

-- agregar columnas AnonId, DNI, Telefono, EsTemporal si faltan
IF COL_LENGTH('dbo.Reservas','AnonId') IS NULL
BEGIN
    ALTER TABLE dbo.Reservas ADD AnonId UNIQUEIDENTIFIER NULL;
END
IF COL_LENGTH('dbo.Reservas','DNI') IS NULL
BEGIN
    ALTER TABLE dbo.Reservas ADD DNI NVARCHAR(20) NULL;
END
IF COL_LENGTH('dbo.Reservas','Telefono') IS NULL
BEGIN
    ALTER TABLE dbo.Reservas ADD Telefono NVARCHAR(30) NULL;
END
IF COL_LENGTH('dbo.Reservas','EsTemporal') IS NULL
BEGIN
    ALTER TABLE dbo.Reservas ADD EsTemporal BIT NOT NULL DEFAULT 1;
END

-- add FK to Usuarios if missing
IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys fk WHERE fk.parent_object_id = OBJECT_ID('dbo.Reservas') AND fk.referenced_object_id = OBJECT_ID('dbo.Usuarios')
)
BEGIN
    ALTER TABLE dbo.Reservas
        ADD CONSTRAINT FK_Reservas_Usuario FOREIGN KEY (UsuarioId) REFERENCES dbo.Usuarios(Id) ON DELETE SET NULL;
END

-- add FK AnonId -> AnonSessions will be created below; add FK only if table exists later
-- (we will add constraint after AnonSessions creation)

-- =======================
-- 3) Preferencias (si no existe)
-- =======================
IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID(N'dbo.Preferencias') AND type = N'U')
BEGIN
    CREATE TABLE dbo.Preferencias (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        UsuarioId INT NOT NULL,
        DatosJson NVARCHAR(MAX) NULL,
        CreadoEn DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        ActualizadoEn DATETIME2 NULL
    );
    ALTER TABLE dbo.Preferencias
        ADD CONSTRAINT FK_Preferencias_Usuario FOREIGN KEY (UsuarioId) REFERENCES dbo.Usuarios(Id) ON DELETE CASCADE;
END

-- índice por UsuarioId
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.Preferencias') AND name = 'IX_Preferencias_Usuario')
BEGIN
    CREATE INDEX IX_Preferencias_Usuario ON dbo.Preferencias(UsuarioId);
END

-- =======================
-- 4) RecomendacionesLog (si no existe)
-- =======================
IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID(N'dbo.RecomendacionesLog') AND type = N'U')
BEGIN
    CREATE TABLE dbo.RecomendacionesLog (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        UsuarioId INT NULL,
        ReservaId INT NULL,
        ExperienciaId INT NULL,
        Score FLOAT NULL,
        CaracteristicasJson NVARCHAR(MAX) NULL,
        CreadoEn DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
    );
END

-- FKs for RecomendacionesLog (if referenced objects exist)
IF OBJECT_ID('dbo.Usuarios') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys fk WHERE fk.parent_object_id = OBJECT_ID('dbo.RecomendacionesLog') AND fk.referenced_object_id = OBJECT_ID('dbo.Usuarios'))
BEGIN
    ALTER TABLE dbo.RecomendacionesLog
        ADD CONSTRAINT FK_RecLog_Usuario FOREIGN KEY (UsuarioId) REFERENCES dbo.Usuarios(Id) ON DELETE SET NULL;
END
IF OBJECT_ID('dbo.Reservas') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys fk WHERE fk.parent_object_id = OBJECT_ID('dbo.RecomendacionesLog') AND fk.referenced_object_id = OBJECT_ID('dbo.Reservas'))
BEGIN
    ALTER TABLE dbo.RecomendacionesLog
        ADD CONSTRAINT FK_RecLog_Reserva FOREIGN KEY (ReservaId) REFERENCES dbo.Reservas(Id) ON DELETE SET NULL;
END
IF OBJECT_ID('dbo.Experiencias') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys fk WHERE fk.parent_object_id = OBJECT_ID('dbo.RecomendacionesLog') AND fk.referenced_object_id = OBJECT_ID('dbo.Experiencias'))
BEGIN
    ALTER TABLE dbo.RecomendacionesLog
        ADD CONSTRAINT FK_RecLog_Experiencia FOREIGN KEY (ExperienciaId) REFERENCES dbo.Experiencias(Id) ON DELETE SET NULL;
END

-- =======================
-- 5) Reportes (si no existe)
-- =======================
IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID(N'dbo.Reportes') AND type = N'U')
BEGIN
    CREATE TABLE dbo.Reportes (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        AdminId INT NOT NULL,
        TipoReporte NVARCHAR(150) NOT NULL,
        ParametrosJson NVARCHAR(MAX) NULL,
        RutaArchivo NVARCHAR(500) NULL,
        CreadoEn DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
    );
    ALTER TABLE dbo.Reportes
        ADD CONSTRAINT FK_Reportes_Admin FOREIGN KEY (AdminId) REFERENCES dbo.Usuarios(Id) ON DELETE NO ACTION;
END

-- =======================
-- 6) AnonSessions (nueva)
-- =======================
IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID(N'dbo.AnonSessions') AND type = N'U')
BEGIN
    CREATE TABLE dbo.AnonSessions (
        AnonId UNIQUEIDENTIFIER NOT NULL PRIMARY KEY DEFAULT NEWID(),
        CreadoEn DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        LastActivity DATETIME2 NULL,
        IpHash NVARCHAR(100) NULL,
        UserAgent NVARCHAR(500) NULL,
        Estado NVARCHAR(30) NOT NULL DEFAULT 'activo'
    );
END

-- Ahora que AnonSessions existe, agregar FK a Reservas.AnonId si no existe
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Reservas') AND name = 'AnonId')
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM sys.foreign_keys fk
        WHERE fk.parent_object_id = OBJECT_ID('dbo.Reservas') AND fk.referenced_object_id = OBJECT_ID('dbo.AnonSessions')
    )
    BEGIN
        ALTER TABLE dbo.Reservas
            ADD CONSTRAINT FK_Reservas_Anon FOREIGN KEY (AnonId) REFERENCES dbo.AnonSessions(AnonId) ON DELETE SET NULL;
    END
END

-- =======================
-- 7) Perfiles (nueva)
-- =======================
IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID(N'dbo.Perfiles') AND type = N'U')
BEGIN
    CREATE TABLE dbo.Perfiles (
        PerfilId INT IDENTITY(1,1) PRIMARY KEY,
        UsuarioId INT NULL,
        AnonId UNIQUEIDENTIFIER NULL,
        Q1 NVARCHAR(100) NULL,
        Q1_Otro NVARCHAR(250) NULL,
        Q2 NVARCHAR(50) NULL,
        Q3 NVARCHAR(100) NULL,
        EstadoPerfilCompleto BIT NOT NULL DEFAULT 0,
        CreadoEn DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        ActualizadoEn DATETIME2 NULL
    );
    ALTER TABLE dbo.Perfiles
        ADD CONSTRAINT FK_Perfiles_Usuario FOREIGN KEY (UsuarioId) REFERENCES dbo.Usuarios(Id) ON DELETE SET NULL;
    ALTER TABLE dbo.Perfiles
        ADD CONSTRAINT FK_Perfiles_Anon FOREIGN KEY (AnonId) REFERENCES dbo.AnonSessions(AnonId) ON DELETE SET NULL;
END

-- =======================
-- 8) Eventos (nueva)
-- =======================
IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID(N'dbo.Eventos') AND type = N'U')
BEGIN
    CREATE TABLE dbo.Eventos (
        EventoId UNIQUEIDENTIFIER NOT NULL PRIMARY KEY DEFAULT NEWID(),
        EventType NVARCHAR(100) NOT NULL,
        UsuarioId INT NULL,
        AnonId UNIQUEIDENTIFIER NULL,
        ConversationId UNIQUEIDENTIFIER NULL,
        SenderId NVARCHAR(200) NOT NULL,
        Payload NVARCHAR(MAX) NULL,
        CreadoEn DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
    );
END

-- =======================
-- 9) Índices útiles
-- =======================
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.Perfiles') AND name = 'IX_Perfiles_AnonId')
BEGIN
    CREATE INDEX IX_Perfiles_AnonId ON dbo.Perfiles(AnonId);
END
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.Eventos') AND name = 'IX_Eventos_UsuarioAnom')
BEGIN
    CREATE INDEX IX_Eventos_UsuarioAnom ON dbo.Eventos(UsuarioId, AnonId);
END

-- =======================
-- 10) Datos iniciales opcionales (ejemplo 3 experiencias si no existen)
-- =======================
IF NOT EXISTS (SELECT 1 FROM dbo.Experiencias WHERE Codigo = '01')
BEGIN
    INSERT INTO dbo.Experiencias (Codigo, Nombre, DuracionMinutos, Descripcion, Activa, CreadoEn, Precio)
    VALUES ('01','MENÚ DEGUSTACIÓN',120,'Experiencia degustación de 6 tiempos',1,SYSUTCDATETIME(),120.00);
END
IF NOT EXISTS (SELECT 1 FROM dbo.Experiencias WHERE Codigo = '02')
BEGIN
    INSERT INTO dbo.Experiencias (Codigo, Nombre, DuracionMinutos, Descripcion, Activa, CreadoEn, Precio)
    VALUES ('02','MENÚ TRADICIONAL',90,'Platos criollos tradicionales',1,SYSUTCDATETIME(),60.00);
END
IF NOT EXISTS (SELECT 1 FROM dbo.Experiencias WHERE Codigo = '03')
BEGIN
    INSERT INTO dbo.Experiencias (Codigo, Nombre, DuracionMinutos, Descripcion, Activa, CreadoEn, Precio)
    VALUES ('03','MENÚ VEGETARIANO',80,'Opciones vegetarianas y veganas',1,SYSUTCDATETIME(),70.00);
END

PRINT 'Migración completada';
GO

select * from Experiencias