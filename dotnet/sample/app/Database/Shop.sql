-- Schema del database dell'app d'esempio, nello stile di SSMS "Genera script".
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[Customers](
    [Id] [int] IDENTITY(1,1) NOT NULL,
    [Name] [nvarchar](100) NOT NULL,
    [Active] [bit] NOT NULL CONSTRAINT [DF_Customers_Active] DEFAULT ((1)),
    [CreditLimit] [decimal](18, 2) NOT NULL CONSTRAINT [DF_Customers_CreditLimit] DEFAULT ((0)),
    CONSTRAINT [PK_Customers] PRIMARY KEY CLUSTERED ([Id] ASC)
)
GO
ALTER TABLE [dbo].[Customers] WITH CHECK ADD CONSTRAINT [CK_Customers_CreditLimit] CHECK (([CreditLimit] >= (0) AND [CreditLimit] <= (50000)))
GO
CREATE UNIQUE NONCLUSTERED INDEX [UX_Customers_Name] ON [dbo].[Customers] ([Name] ASC) WHERE ([Active] = (1))
GO
CREATE TABLE [dbo].[Orders](
    [Id] [int] IDENTITY(1,1) NOT NULL,
    [CustomerId] [int] NOT NULL,
    [Total] [decimal](18, 2) NOT NULL,
    [Status] [nvarchar](20) NOT NULL,
    [CreatedAt] [datetime2](7) NOT NULL CONSTRAINT [DF_Orders_CreatedAt] DEFAULT (sysutcdatetime()),
    CONSTRAINT [PK_Orders] PRIMARY KEY CLUSTERED ([Id] ASC),
    CONSTRAINT [CK_Orders_Total] CHECK ([Total] >= (0)),
    CONSTRAINT [CK_Orders_Status] CHECK ([Status] IN (N'Confirmed', N'PendingApproval', N'Cancelled', N'Invoiced'))
)
GO
ALTER TABLE [dbo].[Orders] WITH CHECK ADD CONSTRAINT [FK_Orders_Customers] FOREIGN KEY([CustomerId]) REFERENCES [dbo].[Customers] ([Id])
GO
CREATE TABLE [dbo].[OrderLines](
    [Id] [int] IDENTITY(1,1) NOT NULL PRIMARY KEY,
    [OrderId] [int] NOT NULL REFERENCES [dbo].[Orders] ([Id]) ON DELETE CASCADE,
    [Quantity] [int] NOT NULL CHECK ([Quantity] > (0)),
    [UnitPrice] [decimal](18, 2) NOT NULL
)
GO
CREATE TABLE [dbo].[Invoices](
    [Id] [int] IDENTITY(1,1) NOT NULL PRIMARY KEY,
    [OrderId] [int] NOT NULL UNIQUE,
    [Amount] [decimal](18, 2) NOT NULL,
    [IssuedAt] [datetime2](7) NOT NULL
)
GO
ALTER TABLE [dbo].[Invoices] ADD CONSTRAINT [FK_Invoices_Orders] FOREIGN KEY([OrderId]) REFERENCES [dbo].[Orders] ([Id])
GO
CREATE TABLE [dbo].[AuditLog](
    [Id] [bigint] IDENTITY(1,1) NOT NULL PRIMARY KEY,
    [TableName] [sysname] NOT NULL,
    [RecordId] [int] NOT NULL,
    [Action] [nvarchar](10) NOT NULL,
    [ChangedAt] [datetime2](7) NOT NULL DEFAULT (sysutcdatetime())
)
GO
CREATE VIEW [dbo].[vw_CustomerOrders]
AS
SELECT c.[Id] AS CustomerId, c.[Name], o.[Id] AS OrderId, o.[Total], o.[Status]
FROM [dbo].[Customers] c
INNER JOIN [dbo].[Orders] o ON o.[CustomerId] = c.[Id]
GO
CREATE FUNCTION [dbo].[fn_CustomerExposure](@customerId int)
RETURNS decimal(18, 2)
AS
BEGIN
    RETURN (SELECT ISNULL(SUM([Total]), 0) FROM [dbo].[Orders]
            WHERE [CustomerId] = @customerId AND [Status] IN (N'Confirmed', N'PendingApproval'))
END
GO
CREATE PROCEDURE [dbo].[usp_LogJob] @job nvarchar(50)
AS
    INSERT INTO [dbo].[AuditLog] ([TableName], [RecordId], [Action]) VALUES (@job, 0, N'JOB')
GO
-- Fattura gli ordini confermati: un ordine si fattura una sola volta
CREATE PROCEDURE [dbo].[usp_InvoiceOrders] @maxOrders int = 500
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @count int;
    SELECT @count = COUNT(*) FROM [dbo].[Orders] WHERE [Status] = N'Confirmed';
    IF @count = 0
    BEGIN
        RAISERROR(N'Nessun ordine da fatturare', 10, 1);
        RETURN;
    END
    INSERT INTO [dbo].[Invoices] ([OrderId], [Amount], [IssuedAt])
    SELECT TOP (@maxOrders) o.[Id], o.[Total], SYSUTCDATETIME()
    FROM [dbo].[Orders] o
    WHERE o.[Status] = N'Confirmed' AND NOT EXISTS (SELECT 1 FROM [dbo].[Invoices] i WHERE i.[OrderId] = o.[Id]);
    UPDATE o SET o.[Status] = N'Invoiced'
    FROM [dbo].[Orders] o INNER JOIN [dbo].[Invoices] i ON i.[OrderId] = o.[Id]
    WHERE o.[Status] = N'Confirmed';
    EXEC [dbo].[usp_LogJob] @job = N'invoice';
END
GO
-- Audit e blocco degli ordini di clienti non attivi
CREATE TRIGGER [dbo].[trg_Orders_Audit] ON [dbo].[Orders]
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM inserted i INNER JOIN [dbo].[Customers] c ON c.[Id] = i.[CustomerId] WHERE c.[Active] = 0)
    BEGIN
        RAISERROR(N'Cliente non attivo: ordine rifiutato', 16, 1);
        ROLLBACK TRANSACTION;
        RETURN;
    END
    INSERT INTO [dbo].[AuditLog] ([TableName], [RecordId], [Action])
    SELECT N'Orders', i.[Id], CASE WHEN EXISTS (SELECT 1 FROM deleted) THEN N'UPDATE' ELSE N'INSERT' END
    FROM inserted i;
END
GO
