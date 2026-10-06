-- Demo database for OrderSync. Run once: sqlcmd -S localhost -E -i seed.sql
IF DB_ID('OrdersDemo') IS NULL CREATE DATABASE OrdersDemo;
GO
USE OrdersDemo;
GO
IF OBJECT_ID('dbo.Orders') IS NULL
CREATE TABLE dbo.Orders (
    OrderId       NVARCHAR(20)  NOT NULL CONSTRAINT PK_Orders PRIMARY KEY,
    CustomerId    NVARCHAR(20)  NOT NULL,
    Sku           NVARCHAR(40)  NOT NULL,
    Quantity      INT           NOT NULL,
    CorrelationId NVARCHAR(200) NULL,
    CreatedUtc    DATETIME2     NOT NULL CONSTRAINT DF_Orders_CreatedUtc DEFAULT SYSUTCDATETIME()
);
GO
-- The order that already exists: sending SO-9931 again produces the primary key violation from the talk.
IF NOT EXISTS (SELECT 1 FROM dbo.Orders WHERE OrderId = 'SO-9931')
INSERT dbo.Orders (OrderId, CustomerId, Sku, Quantity, CorrelationId) VALUES ('SO-9931', '4471', 'WIDGET-01', 3, 'seed');
GO
