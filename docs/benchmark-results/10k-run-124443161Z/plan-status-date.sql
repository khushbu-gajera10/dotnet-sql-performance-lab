SELECT "Id", "OrderDate" FROM orders WHERE "Status" = 'Paid' ORDER BY "OrderDate" DESC, "Id" DESC LIMIT 100;
