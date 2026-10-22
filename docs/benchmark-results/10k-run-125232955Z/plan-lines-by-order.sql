SELECT "OrderId", sum("Quantity" * "UnitPrice") FROM order_lines WHERE "OrderId" = 1 GROUP BY "OrderId";
